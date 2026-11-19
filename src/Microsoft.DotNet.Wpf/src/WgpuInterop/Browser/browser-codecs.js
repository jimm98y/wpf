// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The browser's decoders for the Media Foundation shim (MediaFoundation/Codecs/WebCodecs.cs): WebCodecs'
// VideoDecoder and AudioDecoder, run on the page's thread. The managed side (a worker) queues chunks and
// polls: each decoded frame is copied out as NV12 (video) or interleaved PCM (audio) as it comes, in the
// order it comes, and taken by the managed side when ready. Objects stay here, behind integer handles.
//

const decoders = new Map();
let nextHandle = 1;

// ---- what this browser decodes: isConfigSupported, asked once a configuration ----------------------

const supported = new Map();          // key -> 0 asking, 1 yes, 2 no

function ask(key, make) {
    if (supported.has(key)) return supported.get(key);
    let promise;
    try { promise = make(); }
    catch (e) {
        // a codec whose configuration needs its description (FLAC's): decoded, if at all, once the stream gives one
        const r = /description/i.test(String(e)) ? 1 : 2;
        supported.set(key, r);
        return r;
    }
    supported.set(key, 0);
    promise.then(r => supported.set(key, r && r.supported ? 1 : 2), e => supported.set(key, /description/i.test(String(e)) ? 1 : 2));
    return 0;
}

/** 0 while the browser is asked, 1 where it decodes the codec, 2 where it does not. */
export function supportsVideo(codec) {
    if (typeof VideoDecoder === 'undefined') return 2;
    return ask('v:' + codec, () => VideoDecoder.isConfigSupported({ codec }));
}

export function supportsAudio(codec, sampleRate, channels) {
    if (typeof AudioDecoder === 'undefined') return 2;
    return ask(`a:${codec}:${sampleRate}:${channels}`, () => AudioDecoder.isConfigSupported({ codec, sampleRate, numberOfChannels: channels }));
}

// ---- video ------------------------------------------------------------------------------------------

/** A picture as NV12 of its visible size: of NV12 and I420 as they are, of other formats through RGBX (BT.601, limited). */
async function toNV12(frame) {
    const r = frame.visibleRect;
    const w = r.width, h = r.height, cw = (w + 1) >> 1, ch = (h + 1) >> 1;
    const out = new Uint8Array(w * h + 2 * cw * ch);
    const fmt = frame.format;
    if (fmt === 'NV12') {
        await frame.copyTo(out, { rect: r, layout: [{ offset: 0, stride: w }, { offset: w * h, stride: cw * 2 }] });
    } else if (fmt === 'I420' || fmt === 'I420A') {
        const planes = fmt === 'I420A' ? 4 : 3;
        const tmp = new Uint8Array(w * h * (planes === 4 ? 2 : 1) + 2 * cw * ch);
        const layout = [{ offset: 0, stride: w }, { offset: w * h, stride: cw }, { offset: w * h + cw * ch, stride: cw }];
        if (planes === 4) layout.push({ offset: w * h + 2 * cw * ch, stride: w });
        await frame.copyTo(tmp, { rect: r, layout });
        out.set(tmp.subarray(0, w * h));
        const u = w * h, v = u + cw * ch;
        for (let i = 0; i < cw * ch; i++) { out[u + 2 * i] = tmp[u + i]; out[u + 2 * i + 1] = tmp[v + i]; }
    } else {
        const rgb = new Uint8Array(w * h * 4);
        await frame.copyTo(rgb, { rect: r, format: 'RGBX' });
        for (let y = 0; y < h; y++)
            for (let x = 0; x < w; x++) {
                const o = (y * w + x) * 4;
                out[y * w + x] = ((66 * rgb[o] + 129 * rgb[o + 1] + 25 * rgb[o + 2] + 128) >> 8) + 16;
            }
        for (let y = 0; y < ch; y++)
            for (let x = 0; x < cw; x++) {
                let R = 0, G = 0, B = 0, n = 0;
                for (let dy = 0; dy < 2; dy++) for (let dx = 0; dx < 2; dx++) {
                    const yy = 2 * y + dy, xx = 2 * x + dx;
                    if (yy >= h || xx >= w) continue;
                    const o = (yy * w + xx) * 4; R += rgb[o]; G += rgb[o + 1]; B += rgb[o + 2]; n++;
                }
                R /= n; G /= n; B /= n;
                const c = w * h + y * cw * 2 + x * 2;
                out[c] = Math.max(0, Math.min(255, ((-38 * R - 74 * G + 112 * B + 128) >> 8) + 128));
                out[c + 1] = Math.max(0, Math.min(255, ((112 * R - 94 * G - 18 * B + 128) >> 8) + 128));
            }
    }
    return { w, h, bytes: out };
}

function newEntry(d) {
    // the outputs in the order they come; each filled as its copy finishes
    const e = { ready: false, failed: false };
    d.queue.push(e);
    d.pending++;
    return e;
}

function configureVideo(d) {
    d.dec = new VideoDecoder({
        output: frame => {
            const e = newEntry(d);
            const ts = frame.timestamp;
            toNV12(frame).then(p => { e.w = p.w; e.h = p.h; e.bytes = p.bytes; e.ts = ts; e.ready = true; },
                err => { e.failed = true; e.ready = true; d.error = String(err); console.warn('Media Foundation: a decoded frame not copied: ' + err); })
                .finally(() => { frame.close(); d.pending--; });
        },
        error: err => { d.error = String(err); console.warn('Media Foundation: VideoDecoder: ' + err); },
    });
    d.dec.configure(d.config);
}

/** A decoder of the codec (its picture's size where known); 0 where it cannot be made. */
export function createVideoDecoder(codec, width, height, lowLatency) {
    try {
        const config = { codec, optimizeForLatency: !!lowLatency };
        if (width > 0 && height > 0) { config.codedWidth = width; config.codedHeight = height; }
        const d = { config, queue: [], pending: 0, error: '', flush: 0 };
        configureVideo(d);
        const h = nextHandle++;
        decoders.set(h, d);
        return h;
    } catch (e) {
        console.warn('Media Foundation: no VideoDecoder of ' + codec, e);
        return 0;
    }
}

/** A chunk (its bytes copied): its time in microseconds, whether a key frame. */
export function decodeVideo(handle, bytes, timestamp, key) {
    const d = decoders.get(handle);
    if (!d || d.dec.state === 'closed') return 2;
    try {
        d.dec.decode(new EncodedVideoChunk({ type: key ? 'key' : 'delta', timestamp, data: bytes.slice() }));
        return 0;
    } catch (e) { d.error = String(e); return 1; }
}

/** The state of the queue: [ready (1/0), width, height, time, bytes, decodes queued, copies pending, flush state, rate]. */
function info(d) {
    const e = d.queue[0];
    const ready = !!(e && e.ready);
    return [ready ? 1 : 0, ready ? (e.w || 0) : 0, ready ? (e.h || 0) : 0, ready ? (e.ts || 0) : 0, ready && e.bytes ? e.bytes.length : 0,
        d.dec ? d.dec.decodeQueueSize : 0, d.pending, d.flush, ready ? (e.rate || 0) : 0];
}

/** Into the managed side's array (a MemoryView: written with set, not by index). */
export function videoInfo(handle, out) {
    const d = decoders.get(handle);
    if (!d) return 0;
    out.set(new Float64Array(info(d)));
    return 1;
}

/** The first output's bytes into the buffer, and it gone: its length (0 of a failed one). */
export function takeVideo(handle, buffer) {
    const d = decoders.get(handle);
    if (!d || d.queue.length === 0 || !d.queue[0].ready) return -1;
    const e = d.queue.shift();
    if (e.failed || !e.bytes) return 0;
    buffer.set(e.bytes);
    return e.bytes.length;
}

/** The decoder's flush begun: its state 1 while it runs, 2 when done, 3 when it failed. */
export function flush(handle) {
    const d = decoders.get(handle);
    if (!d) return;
    d.flush = 1;
    d.dec.flush().then(() => { d.flush = 2; }, err => { d.flush = 3; d.error = String(err); });
}

/** Everything let go, the decoder configured again (a key frame needed next). */
export function reset(handle) {
    const d = decoders.get(handle);
    if (!d) return;
    try { d.dec.reset(); d.dec.configure(d.config); } catch (e) { d.error = String(e); }
    d.queue = []; d.flush = 0;
}

export function lastError(handle) {
    const d = decoders.get(handle);
    return d ? d.error : '';
}

export function close(handle) {
    const d = decoders.get(handle);
    if (!d) return;
    try { if (d.dec.state !== 'closed') d.dec.close(); } catch (e) { }
    decoders.delete(handle);
}

// ---- audio ------------------------------------------------------------------------------------------

/**
 * PCM of the decoded data, interleaved: as 16-bit (0), 24-bit packed (1), 32-bit integer (3) or 32-bit float (2);
 * each channel read as f32-planar, which every AudioData converts to.
 */
function toPcm(data, kind) {
    const n = data.numberOfFrames, c = data.numberOfChannels;
    const planes = [];
    for (let i = 0; i < c; i++) {
        const p = new Float32Array(n);
        data.copyTo(p, { planeIndex: i, format: 'f32-planar' });
        planes.push(p);
    }
    const bytesPer = kind === 0 ? 2 : kind === 1 ? 3 : 4;
    const out = new Uint8Array(n * c * bytesPer);
    const view = new DataView(out.buffer);
    let o = 0;
    for (let f = 0; f < n; f++)
        for (let i = 0; i < c; i++) {
            const x = planes[i][f];
            if (kind === 2) { view.setFloat32(o, x, true); o += 4; continue; }
            const scale = kind === 0 ? 32768 : kind === 1 ? 8388608 : 2147483648;
            let v = Math.round(x * scale);
            v = Math.max(-scale, Math.min(scale - 1, v));
            if (kind === 0) view.setInt16(o, v, true);
            else if (kind === 1) { out[o] = v & 255; out[o + 1] = (v >> 8) & 255; out[o + 2] = (v >> 16) & 255; }
            else view.setInt32(o, v, true);
            o += bytesPer;
        }
    return out;
}

/** An audio decoder of the codec, its configuration (description) given; the PCM kind of toPcm. 0 where it cannot be made. */
export function createAudioDecoder(codec, sampleRate, channels, description, kind) {
    try {
        const config = { codec, sampleRate, numberOfChannels: channels };
        if (description && description.length > 0) config.description = description.slice();
        const d = { config, queue: [], pending: 0, error: '', flush: 0, kind };
        d.dec = new AudioDecoder({
            output: data => {
                const e = newEntry(d);
                try { e.bytes = toPcm(data, d.kind); e.ts = data.timestamp; e.w = data.numberOfFrames; e.h = data.numberOfChannels; e.rate = data.sampleRate; }
                catch (err) { e.failed = true; d.error = String(err); }
                finally { e.ready = true; d.pending--; data.close(); }
            },
            error: err => { d.error = String(err); },
        });
        d.dec.configure(config);
        const h = nextHandle++;
        decoders.set(h, d);
        return h;
    } catch (e) {
        console.warn('Media Foundation: no AudioDecoder of ' + codec, e);
        return 0;
    }
}

export function decodeAudio(handle, bytes, timestamp) {
    const d = decoders.get(handle);
    if (!d || d.dec.state === 'closed') return 2;
    try {
        d.dec.decode(new EncodedAudioChunk({ type: 'key', timestamp, data: bytes.slice() }));
        return 0;
    } catch (e) { d.error = String(e); return 1; }
}

/** As videoInfo: [ready, frames, channels, time, bytes, decodes queued, copies pending, flush state, rate]. */
export const audioInfo = videoInfo;

export const takeAudio = takeVideo;

// ---- encoders ---------------------------------------------------------------------------------------

export function supportsVideoEncoder(codec, width, height) {
    if (typeof VideoEncoder === 'undefined') return 2;
    return ask(`ve:${codec}:${width}x${height}`, () => VideoEncoder.isConfigSupported({ codec, width, height }));
}

export function supportsAudioEncoder(codec, sampleRate, channels) {
    if (typeof AudioEncoder === 'undefined') return 2;
    return ask(`ae:${codec}:${sampleRate}:${channels}`, () => AudioEncoder.isConfigSupported({ codec, sampleRate, numberOfChannels: channels }));
}

/** The chunk's bytes queued as an output: its time, whether a key frame (as the entry's w). */
function chunkOut(d, chunk, meta) {
    const e = newEntry(d);
    try {
        const b = new Uint8Array(chunk.byteLength);
        chunk.copyTo(b);
        e.bytes = b; e.ts = chunk.timestamp; e.w = chunk.type === 'key' ? 1 : 0; e.h = chunk.duration || 0;
        const desc = meta && meta.decoderConfig && meta.decoderConfig.description;
        if (desc && !d.description) d.description = desc instanceof ArrayBuffer ? new Uint8Array(desc.slice(0)) : new Uint8Array(desc.buffer.slice(desc.byteOffset, desc.byteOffset + desc.byteLength));
    } catch (err) { e.failed = true; d.error = String(err); }
    finally { e.ready = true; d.pending--; }
}

/** A video encoder: of Annex B (H.264's and HEVC's), its rate (bits a second), frame rate and rate mode ('constant', 'variable'). */
export function createVideoEncoder(codec, width, height, bitrate, framerate, mode) {
    try {
        const config = { codec, width, height, latencyMode: 'quality' };
        if (bitrate > 0) config.bitrate = bitrate;
        if (framerate > 0) config.framerate = framerate;
        if (mode) config.bitrateMode = mode;
        if (codec.startsWith('avc')) config.avc = { format: 'annexb' };
        if (codec.startsWith('hvc') || codec.startsWith('hev')) config.hevc = { format: 'annexb' };
        const d = { config, queue: [], pending: 0, error: '', flush: 0 };
        d.dec = new VideoEncoder({ output: (chunk, meta) => chunkOut(d, chunk, meta), error: err => { d.error = String(err); } });
        d.dec.configure(config);
        const h = nextHandle++;
        decoders.set(h, d);
        return h;
    } catch (e) {
        console.warn('Media Foundation: no VideoEncoder of ' + codec, e);
        return 0;
    }
}

/** An NV12 picture (its bytes copied) encoded: its time in microseconds, a key frame where asked for. */
/** The bit rate changed while encoding: configured again (the frames after it of the new rate). */
export function setEncoderBitrate(handle, bitrate) {
    const d = decoders.get(handle);
    if (!d || d.dec.state !== 'configured') return 2;
    try { d.config.bitrate = bitrate; d.dec.configure(d.config); return 0; }
    catch (e) { d.error = String(e); return 1; }
}

export function encodeVideo(handle, nv12, width, height, timestamp, key) {
    const d = decoders.get(handle);
    if (!d || d.dec.state === 'closed') return 2;
    try {
        const frame = new VideoFrame(nv12.slice(), { format: 'NV12', codedWidth: width, codedHeight: height, timestamp });
        d.dec.encode(frame, { keyFrame: !!key });
        frame.close();
        return 0;
    } catch (e) { d.error = String(e); return 1; }
}

/** An audio encoder of the codec (AAC's 'mp4a.40.x'), of the rate, channels and bit rate. */
export function createAudioEncoder(codec, sampleRate, channels, bitrate) {
    try {
        const config = { codec, sampleRate, numberOfChannels: channels };
        if (bitrate > 0) { config.bitrate = bitrate; config.bitrateMode = 'constant'; }
        const d = { config, queue: [], pending: 0, error: '', flush: 0, rate: sampleRate, channels };
        d.dec = new AudioEncoder({ output: (chunk, meta) => chunkOut(d, chunk, meta), error: err => { d.error = String(err); } });
        d.dec.configure(config);
        const h = nextHandle++;
        decoders.set(h, d);
        return h;
    } catch (e) {
        console.warn('Media Foundation: no AudioEncoder of ' + codec, e);
        return 0;
    }
}

/** Interleaved 16-bit PCM (its bytes copied) encoded: its time in microseconds. */
export function encodeAudio(handle, pcm, frames, timestamp) {
    const d = decoders.get(handle);
    if (!d || d.dec.state === 'closed') return 2;
    try {
        const data = new AudioData({ format: 's16', sampleRate: d.rate, numberOfFrames: frames, numberOfChannels: d.channels, timestamp, data: pcm.slice() });
        d.dec.encode(data);
        data.close();
        return 0;
    } catch (e) { d.error = String(e); return 1; }
}

/** The encoder's configuration (AAC's AudioSpecificConfig, once it has made one) into the buffer: its length, or 0. */
export function encoderConfig(handle, buffer) {
    const d = decoders.get(handle);
    if (!d || !d.description) return 0;
    if (buffer.length >= d.description.length) buffer.set(d.description);
    return d.description.length;
}
