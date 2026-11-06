// A mutex, event or semaphore made, or opened, with an access control list: made and opened by name as .NET makes
// them off Windows (named synchronization objects of the process's user), the list kept with the object's security
// (the System.Security.AccessControl stand-in), not enforced -- what Windows' CreateMutexEx / OpenMutex do here.

using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;

namespace System.Threading
{
    public static class MutexAcl
    {
        public static Mutex Create(bool initiallyOwned, string? name, out bool createdNew, MutexSecurity? mutexSecurity)
        {
            var mutex = new Mutex(initiallyOwned, Local(name), out createdNew);
            if (mutexSecurity != null && createdNew) mutex.SetAccessControl(mutexSecurity);
            return mutex;
        }

        public static Mutex OpenExisting(string name, MutexRights rights) => Mutex.OpenExisting(Local(name)!);

        public static bool TryOpenExisting(string name, MutexRights rights, [NotNullWhen(returnValue: true)] out Mutex? result) =>
            Mutex.TryOpenExisting(Local(name)!, out result);

        // "Global\" names the machine's namespace on Windows; .NET's named objects off Windows take it too
        internal static string? Local(string? name) => name;
    }

    public static class EventWaitHandleAcl
    {
        public static EventWaitHandle Create(bool initialState, EventResetMode mode, string? name, out bool createdNew, EventWaitHandleSecurity? eventSecurity)
        {
            var handle = new EventWaitHandle(initialState, mode, name, out createdNew);
            if (eventSecurity != null && createdNew) handle.SetAccessControl(eventSecurity);
            return handle;
        }

        public static EventWaitHandle OpenExisting(string name, EventWaitHandleRights rights) => EventWaitHandle.OpenExisting(name);

        public static bool TryOpenExisting(string name, EventWaitHandleRights rights, [NotNullWhen(returnValue: true)] out EventWaitHandle? result) =>
            EventWaitHandle.TryOpenExisting(name, out result);
    }

    public static class SemaphoreAcl
    {
        public static Semaphore Create(int initialCount, int maximumCount, string? name, out bool createdNew, SemaphoreSecurity? semaphoreSecurity)
        {
            var semaphore = new Semaphore(initialCount, maximumCount, name, out createdNew);
            if (semaphoreSecurity != null && createdNew) semaphore.SetAccessControl(semaphoreSecurity);
            return semaphore;
        }

        public static Semaphore OpenExisting(string name, SemaphoreRights rights) => Semaphore.OpenExisting(name);

        public static bool TryOpenExisting(string name, SemaphoreRights rights, [NotNullWhen(returnValue: true)] out Semaphore? result) =>
            Semaphore.TryOpenExisting(name, out result);
    }
}

internal static partial class Interop
{
    internal static class Errors
    {
        internal const int ERROR_SUCCESS = 0;
        internal const int ERROR_FILE_NOT_FOUND = 2;
        internal const int ERROR_INVALID_HANDLE = 6;
        internal const int ERROR_INVALID_NAME = 123;
    }
}
