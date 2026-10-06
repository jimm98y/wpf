#!/usr/bin/env python3
"""Checks the hand-authored wl_interface tables in ../WlProtocols.cs against the vendored
protocol XML: opcode ORDER, signature strings, and the types[] array of every request and event.

Nothing in the C# compiler checks any of that, and a mistake is not a compile error -- it is a
Wayland protocol error at runtime that kills the whole connection with a terse message. Run this
after touching WlProtocols.cs:

    python3 validate-tables.py *.xml ../WlProtocols.cs
"""
import re, sys, xml.etree.ElementTree as ET

TYPE_CHAR = {'int':'i','uint':'u','fixed':'f','string':'s','object':'o','new_id':'n','array':'a','fd':'h'}

def from_xml(paths):
    out = {}
    for path in paths:
        root = ET.parse(path).getroot()
        for iface in root.findall('interface'):
            name = iface.get('name'); ver = int(iface.get('version'))
            entry = {'version': ver, 'requests': [], 'events': []}
            for kind, key in (('request','requests'), ('event','events')):
                for msg in iface.findall(kind):
                    sig = ''
                    since = msg.get('since')
                    if since and int(since) > 1:
                        sig += since
                    types = []
                    for arg in msg.findall('arg'):
                        if arg.get('allow-null') == 'true':
                            sig += '?'
                        sig += TYPE_CHAR[arg.get('type')]
                        t = arg.get('interface')
                        types.append(t if arg.get('type') in ('object','new_id') else None)
                    entry[key].append((msg.get('name'), sig, types))
            out[name] = entry
    return out

def from_csharp(path):
    src = open(path).read()
    # Keep only the table, then split on each interface definition.
    src = src[src.index('WlInterfaceDef[] All'):]
    chunks = src.split('new WlInterfaceDef(')[1:]
    out = {}
    for chunk in chunks:
        m = re.match(r'"([a-z0-9_]+)",\s*(\d+),', chunk)
        if not m:
            print("!! could not parse an interface header"); continue
        name, ver, body = m.group(1), int(m.group(2)), chunk
        entry = {'version': ver, 'requests': [], 'events': []}
        # split into requests: / events: sections
        parts = re.split(r'\n\s*events:', body)
        req_body = parts[0]
        evt_body = parts[1] if len(parts) > 1 else ''
        for key, chunk in (('requests', req_body), ('events', evt_body)):
            for mm in re.finditer(r'new WlMsgDef\("([a-z0-9_]+)",\s*"([^"]*)"((?:\s*,\s*(?:"[a-z0-9_]+"|null|\(string\?\)null))*)\s*\)', chunk):
                mname, sig, rest = mm.group(1), mm.group(2), mm.group(3)
                types = []
                for t in re.finditer(r'"([a-z0-9_]+)"|null', rest):
                    types.append(t.group(1) if t.group(1) else None)
                entry[key].append((mname, sig, types))
        out[name] = entry
    return out

xml_defs = from_xml(sys.argv[1:-1])
cs_defs = from_csharp(sys.argv[-1])

# Report coverage. A parser change that silently matches NOTHING would otherwise "pass".
errors = 0
if not cs_defs:
    print("!! parsed 0 interfaces from the C# table -- the parser is broken, not the tables")
    errors += 1
else:
    print(f"checking {len(cs_defs)} interface(s):")
    for n, v in sorted(cs_defs.items()):
        print(f"  {n:34} v{v['version']}  {len(v['requests'])} req / {len(v['events'])} evt")
    print()
for name, cs in sorted(cs_defs.items()):
    if name not in xml_defs:
        print(f"?? {name}: not found in XML (skipped)"); continue
    x = xml_defs[name]
    if cs['version'] > x['version']:
        print(f"!! {name}: declared version {cs['version']} > protocol max {x['version']}"); errors += 1
    for key in ('requests','events'):
        c, e = cs[key], x[key]
        if len(c) != len(e):
            print(f"!! {name}.{key}: {len(c)} declared, {len(e)} in XML"); errors += 1
        for i in range(min(len(c), len(e))):
            cn, csig, ctypes = c[i]
            en, esig, etypes = e[i]
            if cn != en:
                print(f"!! {name}.{key}[{i}]: name '{cn}' != '{en}' (OPCODE MISMATCH)"); errors += 1
            if csig != esig:
                print(f"!! {name}.{key}[{i}] {en}: signature '{csig}' != '{esig}'"); errors += 1
            if ctypes != etypes:
                print(f"!! {name}.{key}[{i}] {en}: types {ctypes} != {etypes}"); errors += 1
print(f"\n{'FAILED: ' + str(errors) + ' problem(s)' if errors else 'OK: all hand-authored tables match the protocol XML'}")
sys.exit(1 if errors else 0)
