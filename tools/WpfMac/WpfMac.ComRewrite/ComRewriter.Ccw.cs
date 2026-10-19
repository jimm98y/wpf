// CCWs: for each rewritten COM interface I, a nested static class I.ComCcw with I's vtable of
// [UnmanagedCallersOnly] stubs (Vtable()), so a managed object implementing I can be handed to native
// code as I. Each stub converts the native arguments the other way round from I.ComImpl, calls I's
// method on the object behind the pointer (Com.CcwTarget), converts the results back, and returns the
// HRESULT -- an exception's, as built-in COM interop does.

using Mono.Cecil;
using Mono.Cecil.Cil;
using WpfMac.ComInterop;

namespace WpfMac.ComRewrite;

public static partial class ComRewriter
{
    private sealed partial class Work
    {
        private readonly Dictionary<TypeDefinition, List<string>> _ccwRefused = new();
        private MethodReference? _fillDispatch;
        private MethodReference? _ccwTarget, _newVtable, _notImplSlot, _hresultOf, _wrap, _strFromBstr, _strFromUni, _strFromAnsi, _uco, _freeBstrM, _strToCoTaskUni;

        private void ImportCcw()
        {
            if (_ccwTarget != null) return;
            var com = typeof(Com);
            MethodReference Imp(string name, params Type[] args) => _m.ImportReference(com.GetMethod(name, args) ?? throw new MissingMethodException(name));
            _ccwTarget = Imp(nameof(Com.CcwTarget), typeof(IntPtr));
            _newVtable = Imp(nameof(Com.NewVtable), typeof(int));
            _fillDispatch = Imp(nameof(Com.FillDispatchSlots), typeof(IntPtr), typeof(RuntimeTypeHandle));
            _notImplSlot = _m.ImportReference(com.GetProperty(nameof(Com.NotImplementedSlot))!.GetGetMethod()!);
            _hresultOf = Imp(nameof(Com.HResultOf), typeof(Exception));
            _wrap = Imp(nameof(Com.Wrap), typeof(IntPtr));
            _strFromBstr = Imp(nameof(Com.StringOfBstr), typeof(IntPtr));
            _strFromUni = Imp(nameof(Com.StringOfUni), typeof(IntPtr));
            _strFromAnsi = Imp(nameof(Com.StringOfAnsi), typeof(IntPtr));
            _freeBstrM = Imp(nameof(Com.FreeBstr), typeof(IntPtr));
            _strToCoTaskUni = _m.ImportReference(typeof(System.Runtime.InteropServices.Marshal).GetMethod(nameof(System.Runtime.InteropServices.Marshal.StringToCoTaskMemUni), new[] { typeof(string) })!);
            _uco = _m.ImportReference(typeof(System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute).GetConstructor(Type.EmptyTypes)!);
        }

        /// <summary>I.ComCcw for <paramref name="iface"/>, whose methods (declaration order) start at slot
        /// <paramref name="first"/>.</summary>
        private void EmitCcw(TypeDefinition iface, int first, List<MethodDefinition> methods)
        {
            ImportCcw();
            var ccw = new TypeDefinition("", "ComCcw", TypeAttributes.NestedPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit, _object);
            iface.NestedTypes.Add(ccw);
            var refused = new List<string>();
            var stubs = new List<MethodDefinition>();
            foreach (var md in methods)
            {
                var stub = Stub(iface, md, out string? why);
                if (why != null) refused.Add($"{md.Name}: {why}");
                ccw.Methods.Add(stub);
                stubs.Add(stub);
            }
            _ccwRefused[iface] = refused;

            // static IntPtr Vtable(): made once
            var field = new FieldDefinition("s_vtable", FieldAttributes.Private | FieldAttributes.Static, _intPtr);
            ccw.Fields.Add(field);
            var vt = new MethodDefinition("Vtable", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, _intPtr);
            ccw.Methods.Add(vt);
            var il = vt.Body.GetILProcessor();
            var p = Local(vt, _intPtr);
            var done = il.Create(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Ldsfld, field);
            il.Emit(OpCodes.Brtrue, done);
            il.Emit(OpCodes.Ldc_I4, first + stubs.Count);
            il.Emit(OpCodes.Call, _newVtable!);
            il.Emit(OpCodes.Stloc, p);
            void Slot(int k, Action load)
            {
                il.Emit(OpCodes.Ldloc, p);
                il.Emit(OpCodes.Ldc_I4, k); il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Sizeof, _intPtr); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Add);
                load();
                il.Emit(OpCodes.Stind_I);
            }
            // a dual interface's or dispinterface's IDispatch (slots 3-6), answered by reflection
            if (first == 7) { il.Emit(OpCodes.Ldloc, p); il.Emit(OpCodes.Ldtoken, iface); il.Emit(OpCodes.Call, _fillDispatch!); }
            else for (int k = 3; k < first; k++) Slot(k, () => il.Emit(OpCodes.Call, _notImplSlot!));
            for (int k = 0; k < stubs.Count; k++) { var s = stubs[k]; Slot(first + k, () => il.Emit(OpCodes.Ldftn, s)); }
            il.Emit(OpCodes.Ldloc, p);
            il.Emit(OpCodes.Stsfld, field);
            il.Append(done);
            il.Emit(OpCodes.Ret);
        }

        /// <summary>The stub for <paramref name="md"/>: native arguments in, I's method called, HRESULT out.
        /// <paramref name="why"/>: what it could not convert (the stub then returns E_NOTIMPL).</summary>
        private MethodDefinition Stub(TypeDefinition iface, MethodDefinition md, out string? why)
        {
            why = null;
            bool preserve = md.IsPreserveSig;
            var args = new List<Arg>();
            foreach (var p in md.Parameters)
            {
                var a = Classify(p.ParameterType, p.MarshalInfo, comDefaults: true, out string? w);
                if (a == null || a.Kind is Kind.BlitArray) { why = $"parameter {p.Name}: {w ?? "an array of unknown length"}"; break; }
                args.Add(a);
            }
            Arg? ret = null;
            bool isVoid = md.ReturnType.FullName == "System.Void";
            if (why == null && !isVoid)
            {
                ret = Classify(md.ReturnType, md.MethodReturnType.MarshalInfo, comDefaults: true, out string? w);
                if (ret == null || ret.Kind is Kind.ByRefBlit or Kind.OutIface or Kind.BlitArray or Kind.RefBool1 or Kind.RefBool2 or Kind.RefBool4 or Kind.RefStrB or Kind.RefStrW or Kind.RefVariant or Kind.StrA
                    || (preserve && ret.Kind == Kind.Variant))
                    why = $"return {w ?? md.ReturnType.Name}";
            }

            var nativeRet = preserve && ret != null ? ret.Native : _int32;
            var stub = new MethodDefinition(md.Name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, why != null ? _int32 : nativeRet);
            stub.CustomAttributes.Add(new CustomAttribute(_uco!));
            stub.Parameters.Add(new ParameterDefinition("self", ParameterAttributes.None, _intPtr));
            stub.Body.InitLocals = true;
            var il = stub.Body.GetILProcessor();
            if (why != null)
            {
                foreach (var p in md.Parameters) stub.Parameters.Add(new ParameterDefinition(_intPtr));      // what the slot is called with does not matter
                il.Emit(OpCodes.Ldc_I4, unchecked((int)0x80004001));
                il.Emit(OpCodes.Ret);
                return stub;
            }
            foreach (var a in args) stub.Parameters.Add(new ParameterDefinition(a.Native));
            int retvalArg = -1;
            if (!preserve && ret != null) { stub.Parameters.Add(new ParameterDefinition(_intPtr)); retvalArg = stub.Parameters.Count - 1; }

            var result = Local(stub, nativeRet);
            var target = Local(stub, iface);
            var after = new List<Action>();
            var tryStart = il.Create(OpCodes.Ldarg_0);
            il.Append(tryStart);
            il.Emit(OpCodes.Call, _ccwTarget!);
            il.Emit(OpCodes.Castclass, iface);
            il.Emit(OpCodes.Stloc, target);
            // the managed arguments
            var loads = new List<Action>();
            for (int i = 0; i < args.Count; i++)
            {
                int n = i + 1;
                var a = args[i];
                var prm = md.Parameters[i];
                bool outOnly = prm.IsOut && !prm.IsIn;
                switch (a.Kind)
                {
                    case Kind.Blit:
                    case Kind.ByRefBlit:          // a native pointer where the method takes a ref: the same address
                        loads.Add(() => il.Emit(OpCodes.Ldarg, n));
                        break;
                    case Kind.Bool4: case Kind.Bool2: case Kind.Bool1:
                        loads.Add(() => { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); });
                        break;
                    case Kind.Iface: case Kind.Unknown:
                        loads.Add(() => { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Call, _wrap!); if (a.Iface != null) il.Emit(OpCodes.Castclass, a.Iface); });
                        break;
                    case Kind.StrW: loads.Add(() => { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Call, _strFromUni!); }); break;
                    case Kind.StrA: loads.Add(() => { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Call, _strFromAnsi!); }); break;
                    case Kind.StrB: loads.Add(() => { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Call, _strFromBstr!); }); break;
                    case Kind.Variant: loads.Add(() => { il.Emit(OpCodes.Ldarga, stub.Parameters[n]); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Call, _fromVariant); }); break;
                    case Kind.SafeArray:
                    {
                        var t = prm.ParameterType;
                        loads.Add(() => { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Ldtoken, t); il.Emit(OpCodes.Call, _fromSafeArray); il.Emit(OpCodes.Castclass, t); });
                        break;
                    }
                    case Kind.RefSafeArray:
                    {
                        var elem = ((ByReferenceType)prm.ParameterType).ElementType;
                        var l = Local(stub, elem);
                        if (!outOnly) { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Ldind_I); il.Emit(OpCodes.Ldtoken, elem); il.Emit(OpCodes.Call, _fromSafeArray); il.Emit(OpCodes.Castclass, elem); il.Emit(OpCodes.Stloc, l); }
                        loads.Add(() => il.Emit(OpCodes.Ldloca, l));
                        after.Add(() =>
                        {
                            // the caller's old array is the callee's to free when it is replaced, as with oleaut32
                            if (!outOnly) { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Ldind_I); il.Emit(OpCodes.Call, _destroySafeArray); }
                            il.Emit(OpCodes.Ldarg, n);
                            il.Emit(OpCodes.Ldloc, l);
                            il.Emit(OpCodes.Ldc_I4, a.Vt);
                            il.Emit(OpCodes.Call, _toSafeArray);
                            il.Emit(OpCodes.Stind_I);
                        });
                        break;
                    }
                    case Kind.OutIface:
                    {
                        var elem = ((ByReferenceType)prm.ParameterType).ElementType;
                        var l = Local(stub, elem);
                        if (!outOnly) { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Ldind_I); il.Emit(OpCodes.Call, _wrap!); if (a.Iface != null) il.Emit(OpCodes.Castclass, a.Iface); il.Emit(OpCodes.Stloc, l); }
                        loads.Add(() => il.Emit(OpCodes.Ldloca, l));
                        after.Add(() =>
                        {
                            // the caller's: AddRef'd (an in/out's old one stays the caller's to release)
                            il.Emit(OpCodes.Ldarg, n);
                            il.Emit(OpCodes.Ldloc, l);
                            if (a.Iface != null) { il.Emit(OpCodes.Ldtoken, a.Iface); il.Emit(OpCodes.Call, _borrow); }
                            else il.Emit(OpCodes.Call, a.Vt == 9 ? _borrowDispatch : _borrowUnknown);
                            il.Emit(OpCodes.Stind_I);
                        });
                        break;
                    }
                    case Kind.RefBool4: case Kind.RefBool2: case Kind.RefBool1:
                    {
                        var l = Local(stub, _m.TypeSystem.Boolean);
                        if (!outOnly)
                        {
                            il.Emit(OpCodes.Ldarg, n);
                            il.Emit(a.Kind == Kind.RefBool4 ? OpCodes.Ldind_I4 : a.Kind == Kind.RefBool2 ? OpCodes.Ldind_I2 : OpCodes.Ldind_U1);
                            il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Cgt_Un); il.Emit(OpCodes.Stloc, l);
                        }
                        loads.Add(() => il.Emit(OpCodes.Ldloca, l));
                        after.Add(() =>
                        {
                            il.Emit(OpCodes.Ldarg, n);
                            il.Emit(OpCodes.Ldloc, l);
                            if (a.Kind == Kind.RefBool2) { il.Emit(OpCodes.Neg); il.Emit(OpCodes.Stind_I2); }
                            else il.Emit(a.Kind == Kind.RefBool4 ? OpCodes.Stind_I4 : OpCodes.Stind_I1);
                        });
                        break;
                    }
                    case Kind.RefStrB: case Kind.RefStrW:
                    {
                        bool b = a.Kind == Kind.RefStrB;
                        var l = Local(stub, _m.TypeSystem.String);
                        if (!outOnly) { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Ldind_I); il.Emit(OpCodes.Call, b ? _strFromBstr! : _strFromUni!); il.Emit(OpCodes.Stloc, l); }
                        loads.Add(() => il.Emit(OpCodes.Ldloca, l));
                        after.Add(() =>
                        {
                            if (!outOnly) { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Ldind_I); il.Emit(OpCodes.Call, b ? _freeBstrM! : _freeCoTask); }
                            il.Emit(OpCodes.Ldarg, n);
                            il.Emit(OpCodes.Ldloc, l);
                            il.Emit(OpCodes.Call, b ? _toBstr : _strToCoTaskUni!);
                            il.Emit(OpCodes.Stind_I);
                        });
                        break;
                    }
                    case Kind.RefVariant:
                    {
                        var l = Local(stub, _object);
                        if (!outOnly) { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Call, _fromVariant); il.Emit(OpCodes.Stloc, l); }
                        loads.Add(() => il.Emit(OpCodes.Ldloca, l));
                        after.Add(() =>
                        {
                            if (!outOnly) { il.Emit(OpCodes.Ldarg, n); il.Emit(OpCodes.Call, _clearVariant); }
                            il.Emit(OpCodes.Ldloc, l);
                            il.Emit(OpCodes.Ldarg, n);
                            il.Emit(OpCodes.Call, _toVariant);
                        });
                        break;
                    }
                }
            }
            il.Emit(OpCodes.Ldloc, target);
            foreach (var l in loads) l();
            il.Emit(OpCodes.Callvirt, md);
            if (ret != null)
            {
                var managed = Local(stub, md.ReturnType);
                il.Emit(OpCodes.Stloc, managed);
                if (preserve)
                {
                    il.Emit(OpCodes.Ldloc, managed);
                    ToNativeValue(il, ret);
                    il.Emit(OpCodes.Stloc, result);
                }
                else
                {
                    // the retval
                    if (ret.Kind == Kind.Variant)
                    {
                        il.Emit(OpCodes.Ldloc, managed);
                        il.Emit(OpCodes.Ldarg, retvalArg);
                        il.Emit(OpCodes.Call, _toVariant);
                    }
                    else
                    {
                        il.Emit(OpCodes.Ldarg, retvalArg);
                        il.Emit(OpCodes.Ldloc, managed);
                        ToNativeValue(il, ret);
                        if (ret.Kind == Kind.Blit) il.Emit(OpCodes.Stobj, ret.Native);
                        else if (ret.Kind == Kind.Bool2) il.Emit(OpCodes.Stind_I2);
                        else if (ret.Kind == Kind.Bool4) il.Emit(OpCodes.Stind_I4);
                        else if (ret.Kind == Kind.Bool1) il.Emit(OpCodes.Stind_I1);
                        else il.Emit(OpCodes.Stind_I);
                    }
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Stloc, result);
                }
            }
            else if (!preserve || nativeRet.FullName == "System.Int32") { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, result); }
            foreach (var a in after) a();
            var end = il.Create(OpCodes.Ldloc, result);
            il.Emit(OpCodes.Leave, end);
            // catch (Exception e): its HRESULT (an int-returning method's)
            var handler = il.Create(OpCodes.Call, _hresultOf!);
            il.Append(handler);
            if (nativeRet.FullName == "System.Int32") il.Emit(OpCodes.Stloc, result);
            else il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Leave, end);
            il.Append(end);
            il.Emit(OpCodes.Ret);
            stub.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
            {
                TryStart = tryStart, TryEnd = handler, HandlerStart = handler, HandlerEnd = end,
                CatchType = _m.ImportReference(typeof(Exception)),
            });
            return stub;
        }

        /// <summary>The managed value on the stack as its native form (for a return or retval).</summary>
        private void ToNativeValue(ILProcessor il, Arg a)
        {
            switch (a.Kind)
            {
                case Kind.Bool2: il.Emit(OpCodes.Neg); il.Emit(OpCodes.Conv_I2); break;            // true -> -1
                case Kind.Bool4: case Kind.Bool1: break;
                case Kind.Iface: il.Emit(OpCodes.Ldtoken, a.Iface!); il.Emit(OpCodes.Call, _borrow); break;
                case Kind.Unknown: il.Emit(OpCodes.Call, a.Vt == 9 ? _borrowDispatch : _borrowUnknown); break;
                case Kind.StrB: il.Emit(OpCodes.Call, _toBstr); break;
                case Kind.StrW: il.Emit(OpCodes.Call, _strToCoTaskUni!); break;
                case Kind.SafeArray: il.Emit(OpCodes.Ldc_I4, a.Vt); il.Emit(OpCodes.Call, _toSafeArray); break;
            }
        }
    }
}
