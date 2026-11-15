' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.

' The strings of Resources/SR.resx, as .NET's build generates them (Arcade's GenerateResxSource) -- this build
' is a plain SDK project. Regenerate when SR.resx changes (eng: python, one property per <data>).

Imports System.Resources

Namespace System

    Friend Module SR
        Private ReadOnly s_resources As New ResourceManager("System.SR", GetType(SR).Assembly)

        Friend Function GetResourceString(resourceKey As String) As String
            Return s_resources.GetString(resourceKey)
        End Function

        Friend Function GetResourceString(resourceKey As String, defaultValue As String) As String
            Dim value As String = s_resources.GetString(resourceKey)
            Return If(value, defaultValue)
        End Function

        Friend ReadOnly Property ID53 As String
            Get
                Return GetResourceString("ID53")
            End Get
        End Property

        Friend ReadOnly Property ID70 As String
            Get
                Return GetResourceString("ID70")
            End Get
        End Property

        Friend ReadOnly Property Argument_InvalidValue1 As String
            Get
                Return GetResourceString("Argument_InvalidValue1")
            End Get
        End Property

        Friend ReadOnly Property Argument_InvalidValueType2 As String
            Get
                Return GetResourceString("Argument_InvalidValueType2")
            End Get
        End Property

        Friend ReadOnly Property Argument_InvalidNullValue1 As String
            Get
                Return GetResourceString("Argument_InvalidNullValue1")
            End Get
        End Property

        Friend ReadOnly Property ProcessNotFound As String
            Get
                Return GetResourceString("ProcessNotFound")
            End Get
        End Property

        Friend ReadOnly Property Mouse_NoMouseIsPresent As String
            Get
                Return GetResourceString("Mouse_NoMouseIsPresent")
            End Get
        End Property

        Friend ReadOnly Property Mouse_NoWheelIsPresent As String
            Get
                Return GetResourceString("Mouse_NoWheelIsPresent")
            End Get
        End Property

        Friend ReadOnly Property IO_FileExists_Path As String
            Get
                Return GetResourceString("IO_FileExists_Path")
            End Get
        End Property

        Friend ReadOnly Property IO_FileNotFound_Path As String
            Get
                Return GetResourceString("IO_FileNotFound_Path")
            End Get
        End Property

        Friend ReadOnly Property IO_FilePathException As String
            Get
                Return GetResourceString("IO_FilePathException")
            End Get
        End Property

        Friend ReadOnly Property General_ArgumentNullException As String
            Get
                Return GetResourceString("General_ArgumentNullException")
            End Get
        End Property

        Friend ReadOnly Property General_PropertyNothing As String
            Get
                Return GetResourceString("General_PropertyNothing")
            End Get
        End Property

        Friend ReadOnly Property ApplicationLog_FreeSpaceError As String
            Get
                Return GetResourceString("ApplicationLog_FreeSpaceError")
            End Get
        End Property

        Friend ReadOnly Property ApplicationLog_FileExceedsMaximumSize As String
            Get
                Return GetResourceString("ApplicationLog_FileExceedsMaximumSize")
            End Get
        End Property

        Friend ReadOnly Property ApplicationLog_ReservedSpaceEncroached As String
            Get
                Return GetResourceString("ApplicationLog_ReservedSpaceEncroached")
            End Get
        End Property

        Friend ReadOnly Property ApplicationLog_NegativeNumber As String
            Get
                Return GetResourceString("ApplicationLog_NegativeNumber")
            End Get
        End Property

        Friend ReadOnly Property ApplicationLogBaseNameNull As String
            Get
                Return GetResourceString("ApplicationLogBaseNameNull")
            End Get
        End Property

        Friend ReadOnly Property ApplicationLogNumberTooSmall As String
            Get
                Return GetResourceString("ApplicationLogNumberTooSmall")
            End Get
        End Property

        Friend ReadOnly Property ApplicationLog_ExhaustedPossibleStreamNames As String
            Get
                Return GetResourceString("ApplicationLog_ExhaustedPossibleStreamNames")
            End Get
        End Property

        Friend ReadOnly Property Network_InvalidUriString As String
            Get
                Return GetResourceString("Network_InvalidUriString")
            End Get
        End Property

        Friend ReadOnly Property Network_BadConnectionTimeout As String
            Get
                Return GetResourceString("Network_BadConnectionTimeout")
            End Get
        End Property

        Friend ReadOnly Property Network_NetworkNotAvailable As String
            Get
                Return GetResourceString("Network_NetworkNotAvailable")
            End Get
        End Property

        Friend ReadOnly Property Network_UploadAddressNeedsFilename As String
            Get
                Return GetResourceString("Network_UploadAddressNeedsFilename")
            End Get
        End Property

        Friend ReadOnly Property Network_DownloadNeedsFilename As String
            Get
                Return GetResourceString("Network_DownloadNeedsFilename")
            End Get
        End Property

        Friend ReadOnly Property ProgressDialogDownloadingTitle As String
            Get
                Return GetResourceString("ProgressDialogDownloadingTitle")
            End Get
        End Property

        Friend ReadOnly Property ProgressDialogUploadingTitle As String
            Get
                Return GetResourceString("ProgressDialogUploadingTitle")
            End Get
        End Property

        Friend ReadOnly Property ProgressDialogDownloadingLabel As String
            Get
                Return GetResourceString("ProgressDialogDownloadingLabel")
            End Get
        End Property

        Friend ReadOnly Property ProgressDialogUploadingLabel As String
            Get
                Return GetResourceString("ProgressDialogUploadingLabel")
            End Get
        End Property

        Friend ReadOnly Property DiagnosticInfo_Memory As String
            Get
                Return GetResourceString("DiagnosticInfo_Memory")
            End Get
        End Property

        Friend ReadOnly Property DiagnosticInfo_FullOSName As String
            Get
                Return GetResourceString("DiagnosticInfo_FullOSName")
            End Get
        End Property

        Friend ReadOnly Property AppModel_NoStartupForm As String
            Get
                Return GetResourceString("AppModel_NoStartupForm")
            End Get
        End Property

        Friend ReadOnly Property AppModel_SingleInstanceCantConnect As String
            Get
                Return GetResourceString("AppModel_SingleInstanceCantConnect")
            End Get
        End Property

        Friend ReadOnly Property AppModel_SplashAndMainFormTheSame As String
            Get
                Return GetResourceString("AppModel_SplashAndMainFormTheSame")
            End Get
        End Property

        Friend ReadOnly Property EnvVarNotFound_Name As String
            Get
                Return GetResourceString("EnvVarNotFound_Name")
            End Get
        End Property

    End Module

End Namespace
