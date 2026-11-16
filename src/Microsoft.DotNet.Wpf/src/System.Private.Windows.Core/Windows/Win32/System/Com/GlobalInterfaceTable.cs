// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices.Marshalling;
using Windows.Win32.System.Com;

namespace Windows.Win32.Foundation;

/// <summary>
///  Wrapper for the COM global interface table.
/// </summary>
internal static unsafe partial class GlobalInterfaceTable
{
    private static readonly IGlobalInterfaceTable* s_globalInterfaceTable;

    // Off Windows there is no OLE and no apartments to marshal between: the table is the process's own,
    // a reference held per cookie, as the global interface table holds one. WPF's DataObject is a COM
    // composition registered here, and making one -- an application starting a drag -- threw for want
    // of OLE32 and took the application down.
    private static readonly bool s_inProcess = !OperatingSystem.IsWindows();
    private static readonly Dictionary<uint, nint> s_table = [];
    private static uint s_nextCookie;

    static GlobalInterfaceTable()
    {
        if (s_inProcess)
        {
            return;
        }

        Guid guid = CLSID.StdGlobalInterfaceTable;

        fixed (IGlobalInterfaceTable** git = &s_globalInterfaceTable)
        {
            PInvokeCore.CoCreateInstance(
                &guid,
                pUnkOuter: null,
                CLSCTX.CLSCTX_INPROC_SERVER,
                IID.Get<IGlobalInterfaceTable>(),
                (void**)git).ThrowOnFailure();
        }
    }

    /// <summary>
    ///  Registers the given <paramref name="interface"/> in the global interface table. This decrements the
    ///  ref count so that the entry in the table will "own" the interface (as it increments the ref count).
    /// </summary>
    /// <returns>The cookie used to refer to the interface in the table.</returns>
    public static uint RegisterInterface<TInterface>(TInterface* @interface)
        where TInterface : unmanaged, IComIID
    {
        uint cookie;
        if (s_inProcess)
        {
            ((IUnknown*)@interface)->AddRef();
            lock (s_table)
            {
                cookie = ++s_nextCookie;
                s_table[cookie] = (nint)@interface;
            }

            return cookie;
        }

        HRESULT hr = s_globalInterfaceTable->RegisterInterfaceInGlobal(
            (IUnknown*)@interface,
            IID.Get<TInterface>(),
            &cookie);
        hr.ThrowOnFailure();
        return cookie;
    }

    /// <summary>
    ///  Gets an agile interface for the <paramref name="cookie"/> that was given back by
    ///  <see cref="RegisterInterface{TInterface}(TInterface*)"/>
    /// </summary>
    public static ComScope<TInterface> GetInterface<TInterface>(uint cookie, out HRESULT result)
        where TInterface : unmanaged, IComIID
    {
        ComScope<TInterface> @interface = new(null);
        result = GetFromGlobal(cookie, *IID.Get<TInterface>(), out void* found);
        if (result.Succeeded)
        {
            @interface = new((TInterface*)found);
        }

        return @interface;
    }

    /// <summary>GetInterfaceFromGlobal: the registered interface, queried for <paramref name="iid"/>.</summary>
    private static HRESULT GetFromGlobal(uint cookie, in Guid iid, out void* ppObj)
    {
        if (!s_inProcess)
        {
            return s_globalInterfaceTable->GetInterfaceFromGlobal(cookie, iid, out ppObj);
        }

        ppObj = null;
        nint registered;
        lock (s_table)
        {
            if (!s_table.TryGetValue(cookie, out registered))
            {
                return HRESULT.E_INVALIDARG;
            }
        }

        fixed (Guid* riid = &iid)
        fixed (void** result = &ppObj)
        {
            return ((IUnknown*)registered)->QueryInterface(riid, result);
        }
    }

    /// <summary>
    ///  Revokes the interface registered with <see cref="RegisterInterface{TInterface}(TInterface*)"/>.
    ///  This will decrement the ref count for the interface.
    /// </summary>
    public static HRESULT RevokeInterface(uint cookie)
    {
        if (s_inProcess)
        {
            nint registered;
            lock (s_table)
            {
                if (!s_table.Remove(cookie, out registered))
                {
                    return HRESULT.E_INVALIDARG;
                }
            }

            ((IUnknown*)registered)->Release();
            return HRESULT.S_OK;
        }

        HRESULT hr = s_globalInterfaceTable->RevokeInterfaceFromGlobal(cookie);
        Debug.WriteLineIf(hr.Failed, $"{nameof(GlobalInterfaceTable)}: Failed to revoke interface.");
        return hr;
    }

    /// <summary>
    ///  Creates a new instance of an <see cref="IIUnknownStrategy"/> for <see cref="StrategyBasedComWrappers"/>
    ///  that uses the Global Interface Table.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   The returned instance should not be cached.
    ///  </para>
    /// </remarks>
    public static IIUnknownStrategy CreateUnknownStrategy() => new UnknownStrategy();
}
