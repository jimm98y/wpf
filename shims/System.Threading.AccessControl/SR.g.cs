// Generated from dotnet/runtime src/libraries/System.Threading.AccessControl/src/Resources/Strings.resx (MIT).
namespace System
{
    internal static class SR
    {
        internal const string ArgumentOutOfRange_Enum = "Enum value was out of legal range.";
        internal const string ArgumentOutOfRange_NeedNonNegNum = "Non-negative number required.";
        internal const string ArgumentOutOfRange_NeedPosNum = "Positive number required.";
        internal const string Argument_EmptyName = "Empty name is not legal.";
        internal const string Argument_SemaphoreInitialMaximum = "The initial count for the semaphore must be greater than or equal to zero and less than the maximum count.";
        internal const string Argument_WaitHandleNameTooLong = "The length of the name exceeds the maximum limit.";
        internal const string IO_AlreadyExists_Name = "Cannot create '{0}' because a file or directory with the same name already exists.";
        internal const string IO_FileExists_Name = "The file '{0}' already exists.";
        internal const string IO_FileNotFound = "Unable to find the specified file.";
        internal const string IO_FileNotFound_FileName = "Could not find file '{0}'.";
        internal const string IO_PathNotFound_NoPathName = "Could not find a part of the path.";
        internal const string IO_PathNotFound_Path = "Could not find a part of the path '{0}'.";
        internal const string IO_PathTooLong = "The specified file name or path is too long, or a component of the specified path is too long.";
        internal const string IO_PathTooLong_Path = "The path '{0}' is too long, or a component of the specified path is too long.";
        internal const string IO_SharingViolation_File = "The process cannot access the file '{0}' because it is being used by another process.";
        internal const string IO_SharingViolation_NoFileName = "The process cannot access the file because it is being used by another process.";
        internal const string PlatformNotSupported_AccessControl = "Access Control List (ACL) APIs are part of resource management on Windows and are not supported on this platform.";
        internal const string Threading_WaitHandleCannotBeOpenedException_InvalidHandle = "A WaitHandle with system-wide name '{0}' cannot be created. A WaitHandle of a different type might have the same name.";
        internal const string UnauthorizedAccess_IODenied_NoPathName = "Access to the path is denied.";
        internal const string UnauthorizedAccess_IODenied_Path = "Access to the path '{0}' is denied.";
        internal const string WaitHandleCannotBeOpenedException_InvalidHandle = "A WaitHandle with system-wide name '{0}' cannot be created. A WaitHandle of a different type might have the same name.";
        internal static string Format(string format, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);
    }
}
