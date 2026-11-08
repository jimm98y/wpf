// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;

namespace System.Net
{
    public class HttpListenerTimeoutManager
    {
        private TimeSpan _drainEntityBody = TimeSpan.Zero;
        private TimeSpan _idleConnection = TimeSpan.Zero;
        // http.sys keeps these for the request queue; here the machine's http.sys service, in front, applies its own
        // limits, and they are kept as http.sys keeps them -- set and read back -- rather than refused: a server that
        // sets them (a point manager's host sets MinSendBytesPerSecond) started on Windows and must start here
        private TimeSpan _entityBody = TimeSpan.Zero;
        private TimeSpan _headerWait = TimeSpan.Zero;
        private long _minSendBytesPerSecond;
        private TimeSpan _requestQueue = TimeSpan.Zero;

        internal HttpListenerTimeoutManager(HttpListener _) { }

        public TimeSpan DrainEntityBody
        {
            get => _drainEntityBody;
            set
            {
                // Managed implementation currently doesn't pool connections,
                // so this is a nop other than roundtripping the value.
                ValidateTimeout(value);
                _drainEntityBody = value;
            }
        }

        public TimeSpan IdleConnection
        {
            get => _idleConnection;
            set
            {
                // Managed implementation currently doesn't pool connections,
                // so this is a nop other than roundtripping the value.
                ValidateTimeout(value);
                _idleConnection = value;
            }
        }

        public TimeSpan EntityBody
        {
            get => _entityBody;
            set
            {
                ValidateTimeout(value);
                _entityBody = value;
            }
        }

        public TimeSpan HeaderWait
        {
            get => _headerWait;
            set
            {
                ValidateTimeout(value);
                _headerWait = value;
            }
        }

        public long MinSendBytesPerSecond
        {
            get => _minSendBytesPerSecond;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, uint.MaxValue);
                _minSendBytesPerSecond = value;
            }
        }

        public TimeSpan RequestQueue
        {
            get => _requestQueue;
            set
            {
                ValidateTimeout(value);
                _requestQueue = value;
            }
        }

        private static void ValidateTimeout(TimeSpan value)
        {
            long timeoutValue = Convert.ToInt64(value.TotalSeconds);
            ArgumentOutOfRangeException.ThrowIfNegative(timeoutValue, nameof(value));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutValue, ushort.MaxValue, nameof(value));
        }
    }
}
