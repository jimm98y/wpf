// A mutex, event or semaphore made, or opened, with an access control list.
//
// Windows (WindowsAcl): the kernel's own objects, made with the list as their security descriptor (CreateMutexEx,
// CreateEventEx, CreateSemaphoreEx) and opened with the rights asked for (OpenMutex, ...), as dotnet/runtime's
// System.Threading.AccessControl makes them -- this assembly is one of the Windows Desktop Runtime's, which an
// installation does not need.
//
// Elsewhere: made and opened by name as .NET makes them off Windows (named synchronization objects of the process's
// user), the list kept with the object's security (the System.Security.AccessControl stand-in), not enforced.

using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;

namespace System.Threading
{
    public static class MutexAcl
    {
        public static Mutex Create(bool initiallyOwned, string? name, out bool createdNew, MutexSecurity? mutexSecurity)
        {
            if (OperatingSystem.IsWindows()) return WindowsAcl.CreateMutex(initiallyOwned, name, out createdNew, mutexSecurity);
            var mutex = new Mutex(initiallyOwned, name, out createdNew);
            if (mutexSecurity != null && createdNew) mutex.SetAccessControl(mutexSecurity);
            return mutex;
        }

        public static Mutex OpenExisting(string name, MutexRights rights) =>
            OperatingSystem.IsWindows() ? WindowsAcl.OpenMutex(name, rights) : Mutex.OpenExisting(name);

        public static bool TryOpenExisting(string name, MutexRights rights, [NotNullWhen(returnValue: true)] out Mutex? result)
        {
            if (!OperatingSystem.IsWindows()) return Mutex.TryOpenExisting(name, out result);
            result = WindowsAcl.TryOpenMutex(name, rights);
            return result != null;
        }
    }

    public static class EventWaitHandleAcl
    {
        public static EventWaitHandle Create(bool initialState, EventResetMode mode, string? name, out bool createdNew, EventWaitHandleSecurity? eventSecurity)
        {
            if (OperatingSystem.IsWindows()) return WindowsAcl.CreateEvent(initialState, mode, name, out createdNew, eventSecurity);
            var handle = new EventWaitHandle(initialState, mode, name, out createdNew);
            if (eventSecurity != null && createdNew) handle.SetAccessControl(eventSecurity);
            return handle;
        }

        public static EventWaitHandle OpenExisting(string name, EventWaitHandleRights rights) =>
            OperatingSystem.IsWindows() ? WindowsAcl.OpenEvent(name, rights) : EventWaitHandle.OpenExisting(name);

        public static bool TryOpenExisting(string name, EventWaitHandleRights rights, [NotNullWhen(returnValue: true)] out EventWaitHandle? result)
        {
            if (!OperatingSystem.IsWindows()) return EventWaitHandle.TryOpenExisting(name, out result);
            result = WindowsAcl.TryOpenEvent(name, rights);
            return result != null;
        }
    }

    public static class SemaphoreAcl
    {
        public static Semaphore Create(int initialCount, int maximumCount, string? name, out bool createdNew, SemaphoreSecurity? semaphoreSecurity)
        {
            if (OperatingSystem.IsWindows()) return WindowsAcl.CreateSemaphore(initialCount, maximumCount, name, out createdNew, semaphoreSecurity);
            var semaphore = new Semaphore(initialCount, maximumCount, name, out createdNew);
            if (semaphoreSecurity != null && createdNew) semaphore.SetAccessControl(semaphoreSecurity);
            return semaphore;
        }

        public static Semaphore OpenExisting(string name, SemaphoreRights rights) =>
            OperatingSystem.IsWindows() ? WindowsAcl.OpenSemaphore(name, rights) : Semaphore.OpenExisting(name);

        public static bool TryOpenExisting(string name, SemaphoreRights rights, [NotNullWhen(returnValue: true)] out Semaphore? result)
        {
            if (!OperatingSystem.IsWindows()) return Semaphore.TryOpenExisting(name, out result);
            result = WindowsAcl.TryOpenSemaphore(name, rights);
            return result != null;
        }
    }
}

internal static partial class Interop
{
    internal static class Errors
    {
        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_PATH_NOT_FOUND = 3;
        internal const int ERROR_ACCESS_DENIED = 5;
        internal const int ERROR_INVALID_HANDLE = 6;
        internal const int ERROR_INVALID_PARAMETER = 87;
        internal const int ERROR_FILENAME_EXCED_RANGE = 206;
        internal const int ERROR_INVALID_NAME = 123;
        internal const int ERROR_ALREADY_EXISTS = 183;
    }
}
