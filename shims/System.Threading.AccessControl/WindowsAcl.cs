// Windows: the kernel's mutexes, events and semaphores, made with an access control list and opened with given rights,
// as dotnet/runtime's System.Threading.AccessControl (MIT) makes them -- the list's binary security descriptor in
// the SECURITY_ATTRIBUTES, the handle given to a .NET wait handle, Windows' errors reported as .NET reports them.

using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace System.Threading
{
    internal static unsafe class WindowsAcl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public void* lpSecurityDescriptor;
            public int bInheritHandle;
        }

        private const string Kernel = "kernel32.dll";
        private const uint MUTEX_ALL_ACCESS = 0x1F0001, EVENT_ALL_ACCESS = 0x1F0003, SEMAPHORE_ALL_ACCESS = 0x1F0003;
        private const uint CREATE_MUTEX_INITIAL_OWNER = 0x1, CREATE_EVENT_MANUAL_RESET = 0x1, CREATE_EVENT_INITIAL_SET = 0x2;
        private const int MaxPath = 260;

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateMutexExW(SECURITY_ATTRIBUTES* attributes, string? name, uint flags, uint access);

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateEventExW(SECURITY_ATTRIBUTES* attributes, string? name, uint flags, uint access);

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateSemaphoreExW(SECURITY_ATTRIBUTES* attributes, int initial, int maximum, string? name, uint flags, uint access);

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle OpenMutexW(uint access, bool inherit, string name);

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle OpenEventW(uint access, bool inherit, string name);

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle OpenSemaphoreW(uint access, bool inherit, string name);

        // ---- made -------------------------------------------------------------------------------------------

        public static Mutex CreateMutex(bool initiallyOwned, string? name, out bool createdNew, MutexSecurity? security)
        {
            CheckName(name);
            SafeWaitHandle handle = WithAttributes(security, a => CreateMutexExW(a, name, initiallyOwned ? CREATE_MUTEX_INITIAL_OWNER : 0, MUTEX_ALL_ACCESS), out createdNew, name);
            return Wrap(new Mutex(false), handle);
        }

        public static EventWaitHandle CreateEvent(bool initialState, EventResetMode mode, string? name, out bool createdNew, EventWaitHandleSecurity? security)
        {
            if (mode is not (EventResetMode.AutoReset or EventResetMode.ManualReset)) throw new ArgumentOutOfRangeException(nameof(mode));
            CheckName(name);
            uint flags = (mode == EventResetMode.ManualReset ? CREATE_EVENT_MANUAL_RESET : 0) | (initialState ? CREATE_EVENT_INITIAL_SET : 0);
            SafeWaitHandle handle = WithAttributes(security, a => CreateEventExW(a, name, flags, EVENT_ALL_ACCESS), out createdNew, name);
            return Wrap(new EventWaitHandle(false, mode), handle);
        }

        public static Semaphore CreateSemaphore(int initialCount, int maximumCount, string? name, out bool createdNew, SemaphoreSecurity? security)
        {
            if (initialCount < 0) throw new ArgumentOutOfRangeException(nameof(initialCount), "Non-negative number required.");
            if (maximumCount < 1) throw new ArgumentOutOfRangeException(nameof(maximumCount), "Positive number required.");
            if (initialCount > maximumCount) throw new ArgumentException("The initial count for the semaphore must be greater than or equal to zero and less than the maximum count.");
            CheckName(name);
            SafeWaitHandle handle = WithAttributes(security, a => CreateSemaphoreExW(a, initialCount, maximumCount, name, 0, SEMAPHORE_ALL_ACCESS), out createdNew, name);
            return Wrap(new Semaphore(0, 1), handle);
        }

        // ---- opened -----------------------------------------------------------------------------------------

        public static Mutex OpenMutex(string name, MutexRights rights) => Wrap(new Mutex(false), Open(name, n => OpenMutexW((uint)rights, false, n), throwIfMissing: true)!);

        public static Mutex? TryOpenMutex(string name, MutexRights rights) =>
            Open(name, n => OpenMutexW((uint)rights, false, n), throwIfMissing: false) is { } h ? Wrap(new Mutex(false), h) : null;

        public static EventWaitHandle OpenEvent(string name, EventWaitHandleRights rights) =>
            Wrap(new EventWaitHandle(false, EventResetMode.AutoReset), Open(name, n => OpenEventW((uint)rights, false, n), throwIfMissing: true)!);

        public static EventWaitHandle? TryOpenEvent(string name, EventWaitHandleRights rights) =>
            Open(name, n => OpenEventW((uint)rights, false, n), throwIfMissing: false) is { } h ? Wrap(new EventWaitHandle(false, EventResetMode.AutoReset), h) : null;

        public static Semaphore OpenSemaphore(string name, SemaphoreRights rights) =>
            Wrap(new Semaphore(0, 1), Open(name, n => OpenSemaphoreW((uint)rights, false, n), throwIfMissing: true)!);

        public static Semaphore? TryOpenSemaphore(string name, SemaphoreRights rights) =>
            Open(name, n => OpenSemaphoreW((uint)rights, false, n), throwIfMissing: false) is { } h ? Wrap(new Semaphore(0, 1), h) : null;

        // ---- helpers ----------------------------------------------------------------------------------------

        private delegate SafeWaitHandle Make(SECURITY_ATTRIBUTES* attributes);

        // the object made with the list as its security descriptor (none: the creator's default); whether it was made
        // or was there already
        private static SafeWaitHandle WithAttributes(NativeObjectSecurity? security, Make make, out bool createdNew, string? name)
        {
            byte[]? descriptor = security?.GetSecurityDescriptorBinaryForm();
            SafeWaitHandle handle;
            int error;
            fixed (byte* d = descriptor)
            {
                var attributes = new SECURITY_ATTRIBUTES { nLength = sizeof(SECURITY_ATTRIBUTES), lpSecurityDescriptor = d };
                handle = make(descriptor != null ? &attributes : null);
                error = Marshal.GetLastPInvokeError();
            }
            if (handle.IsInvalid)
            {
                handle.Dispose();
                // a name already taken by an object of another kind
                if (error == Interop.Errors.ERROR_INVALID_HANDLE) throw new WaitHandleCannotBeOpenedException($"A WaitHandle with system-wide name '{name}' cannot be created. A WaitHandle of a different type might have the same name.");
                throw ErrorFor(error, name);
            }
            createdNew = error != Interop.Errors.ERROR_ALREADY_EXISTS;
            return handle;
        }

        private static SafeWaitHandle? Open(string name, Func<string, SafeWaitHandle> open, bool throwIfMissing)
        {
            ArgumentNullException.ThrowIfNull(name);
            if (name.Length == 0) throw new ArgumentException("Empty name is not legal.", nameof(name));
            CheckName(name);
            SafeWaitHandle handle = open(name);
            if (!handle.IsInvalid) return handle;
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (error is Interop.Errors.ERROR_FILE_NOT_FOUND or Interop.Errors.ERROR_INVALID_NAME or Interop.Errors.ERROR_PATH_NOT_FOUND)
            {
                if (!throwIfMissing) return null;
                throw new WaitHandleCannotBeOpenedException($"No handle of the given name exists.");
            }
            if (error == Interop.Errors.ERROR_INVALID_HANDLE)
            {
                if (!throwIfMissing) return null;
                throw new WaitHandleCannotBeOpenedException($"A WaitHandle with system-wide name '{name}' cannot be created. A WaitHandle of a different type might have the same name.");
            }
            throw ErrorFor(error, name);
        }

        // the .NET wait handle on the kernel's handle: the one it was made with given up
        private static T Wrap<T>(T waitHandle, SafeWaitHandle handle) where T : WaitHandle
        {
            SafeWaitHandle made = waitHandle.SafeWaitHandle;
            waitHandle.SafeWaitHandle = handle;
            made.Dispose();
            return waitHandle;
        }

        private static void CheckName(string? name)
        {
            if (name != null && name.Length > MaxPath) throw new ArgumentException($"The name can be no more than {MaxPath} characters in length.", nameof(name));
        }

        private static Exception ErrorFor(int error, string? name) => error switch
        {
            Interop.Errors.ERROR_ACCESS_DENIED => new UnauthorizedAccessException($"Access to the path '{name}' is denied."),
            Interop.Errors.ERROR_FILENAME_EXCED_RANGE => new PathTooLongException(),
            Interop.Errors.ERROR_INVALID_PARAMETER => new ArgumentException(new Runtime.InteropServices.COMException(null, error).Message),
            _ => new IOException(Marshal.GetPInvokeErrorMessage(error), error),
        };
    }
}
