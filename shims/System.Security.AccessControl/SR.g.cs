// Generated from dotnet/runtime src/libraries/System.Security.AccessControl/src/Resources/Strings.resx (MIT).
namespace System
{
    internal static class SR
    {
        internal const string AccessControl_AclTooLong = "Length of the access control list exceed the allowed maximum.";
        internal const string AccessControl_InvalidAccessRuleType = "The access rule is not the correct type.";
        internal const string AccessControl_InvalidAuditRuleType = "The audit rule is not the correct type.";
        internal const string AccessControl_InvalidOwner = "The security identifier is not allowed to be the owner of this object.";
        internal const string AccessControl_InvalidGroup = "The security identifier is not allowed to be the primary group of this object.";
        internal const string AccessControl_InvalidHandle = "The supplied handle is invalid. This can happen when trying to set an ACL on an anonymous kernel object.";
        internal const string AccessControl_InvalidSecurityDescriptorRevision = "Security descriptor with revision other than '1' are not legal.";
        internal const string AccessControl_InvalidSecurityDescriptorSelfRelativeForm = "Security descriptor must be in the self-relative form.";
        internal const string AccessControl_InvalidSidInSDDLString = "The SDDL string contains an invalid sid or a sid that cannot be translated.";
        internal const string AccessControl_MustSpecifyContainerAcl = "The named parameter must be a container ACL.";
        internal const string AccessControl_MustSpecifyDirectoryObjectAcl = "The named parameter must be a directory-object ACL.";
        internal const string AccessControl_MustSpecifyLeafObjectAcl = "The named parameter must be a non-container ACL.";
        internal const string AccessControl_MustSpecifyNonDirectoryObjectAcl = "The named parameter must be a non-directory-object ACL.";
        internal const string AccessControl_NoAssociatedSecurity = "Unable to perform a security operation on an object that has no associated security. This can happen when trying to get an ACL of an anonymous kernel object.";
        internal const string AccessControl_UnexpectedError = "Method failed with unexpected error code {0}.";
        internal const string Arg_EnumAtLeastOneFlag = "Must set at least one flag.";
        internal const string Arg_EnumIllegalVal = "Illegal enum value: {0}.";
        internal const string Arg_InvalidOperationException = "Operation is not valid due to the current state of the object.";
        internal const string Arg_MustBeIdentityReferenceType = "Type must be an IdentityReference, such as NTAccount or SecurityIdentifier.";
        internal const string Argument_ArgumentZero = "Argument cannot be zero.";
        internal const string Argument_InvalidAnyFlag = "No flags can be set.";
        internal const string Argument_InvalidEnumValue = "The value '{0}' is not valid for this usage of the type {1}.";
        internal const string Argument_InvalidName = "Invalid name.";
        internal const string Argument_InvalidPrivilegeName = "Privilege '{0}' is not valid on this system.";
        internal const string Argument_InvalidSafeHandle = "The SafeHandle is invalid.";
        internal const string ArgumentException_InvalidAceBinaryForm = "The binary form of an ACE object is invalid.";
        internal const string ArgumentException_InvalidAclBinaryForm = "The binary form of an ACL object is invalid.";
        internal const string ArgumentException_InvalidSDSddlForm = "The SDDL form of a security descriptor object is invalid.";
        internal const string ArgumentOutOfRange_ArrayLength = "The length of the array must be between {0} and {1}, inclusive.";
        internal const string ArgumentOutOfRange_ArrayLengthMultiple = "The length of the array must be a multiple of {0}.";
        internal const string ArgumentOutOfRange_ArrayTooSmall = "Destination array is not long enough to copy all the required data. Check array length and offset.";
        internal const string ArgumentOutOfRange_Enum = "Enum value was out of legal range.";
        internal const string ArgumentOutOfRange_InvalidUserDefinedAceType = "User-defined ACEs must not have a well-known ACE type.";
        internal const string InvalidOperation_ModificationOfNonCanonicalAcl = "This access control list is not in canonical form and therefore cannot be modified.";
        internal const string InvalidOperation_MustBeSameThread = "This operation must take place on the same thread on which the object was created.";
        internal const string InvalidOperation_MustLockForReadOrWrite = "Object must be locked for read or write.";
        internal const string InvalidOperation_MustLockForWrite = "Object must be locked for read.";
        internal const string InvalidOperation_MustRevertPrivilege = "Must revert the privilege prior to attempting this operation.";
        internal const string InvalidOperation_NoSecurityDescriptor = "The object does not contain a security descriptor.";
        internal const string InvalidOperation_OnlyValidForDS = "Adding ACEs with Object Flags and Object GUIDs is only valid for directory-object ACLs.";
        internal const string InvalidOperation_DisconnectedPipe = "The pipe has been disconnected.";
        internal const string NotSupported_SetMethod = "The 'set' method is not supported on this property.";
        internal const string PrivilegeNotHeld_Default = "The process does not possess some privilege required for this operation.";
        internal const string PrivilegeNotHeld_Named = "The process does not possess the '{0}' privilege which is required for this operation.";
        internal const string Rank_MultiDimNotSupported = "Only single dimension arrays are supported here.";
        internal const string PlatformNotSupported_AccessControl = "Access Control List (ACL) APIs are part of resource management on Windows and are not supported on this platform.";
        internal static string Format(string format, params object[] args) => string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args);
    }
}
