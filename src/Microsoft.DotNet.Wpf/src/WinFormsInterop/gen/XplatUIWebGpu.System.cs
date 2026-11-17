// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// What the driver asks the system on an application's behalf: the power supply (SystemInformation.PowerStatus,
// which threw), the lock keys (Control.IsKeyLocked, always false) and the beep (MessageBox, a text box refusing a
// key: AudibleAlert did nothing).
//

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace System.Windows.Forms
{
	internal partial class XplatUIWebGpu
	{
		// ---- the lock keys -------------------------------------------------------------------

		/// <summary>The host's answer to GetKeyState(vk) &amp; 1 for VK_CAPITAL, VK_NUMLOCK and VK_SCROLL: null where
		/// it cannot tell.</summary>
		internal static Func<int, bool?> KeyLockedProvider;

		internal override bool IsKeyLocked(VirtualKeys key)
		{
			try { return KeyLockedProvider?.Invoke((int)key) ?? false; }
			catch (Exception) { return false; }
		}

		// ---- SendKeys --------------------------------------------------------------------------

		/// <summary>
		/// SendKeys' keystrokes, as the keyboard would type them into the focused window: a key is WM_KEYDOWN, the
		/// character it types (WM_CHAR, unless the key was taken -- a shortcut, a navigation key) and WM_KEYUP; Shift,
		/// Ctrl and Alt held in the sequence are held for what follows (^c is Ctrl+C; a letter under Ctrl types its
		/// control character, under Alt it is WM_SYSKEYDOWN and WM_SYSCHAR). A character to type finds its key as
		/// VkKeyScan does for letters, digits and the space, with Shift for a capital. Nothing was sent at all.
		/// </summary>
		internal override int SendInput(IntPtr hwnd, System.Collections.Queue keys)
		{
			Keys before = _modifierKeys, held = Keys.None;
			int sent = 0;
			void Mods() => SetModifierKeys((before & ~(Keys.Shift | Keys.Control | Keys.Alt)) | held);
			try
			{
				while (keys.Count > 0)
				{
					var m = (MSG)keys.Dequeue();
					int code = (int)(long)m.wParam;
					sent++;
					switch (m.message)
					{
						case Msg.WM_KEYDOWN:
						case Msg.WM_KEYUP:
						{
							bool down = m.message == Msg.WM_KEYDOWN;
							Keys mod = code switch { 0x10 => Keys.Shift, 0x11 => Keys.Control, 0x12 => Keys.Alt, _ => Keys.None };
							if (mod != Keys.None)
							{
								held = down ? held | mod : held & ~mod;
								Mods();
								continue;
							}
							if (!down) { InjectKeyUp(code); continue; }
							if ((held & Keys.Alt) != 0) { InjectSysKeyDown(code); continue; }
							// (InjectKeyDown types Enter, Backspace, Tab and Escape itself, as TranslateMessage does)
							if (InjectKeyDown(code)) continue;
							if (code == 0x20) InjectChar(' ');
							continue;
						}
						case Msg.WM_CHAR:
						{
							char c = (char)code;
							if ((held & Keys.Shift) != 0 && c is >= 'a' and <= 'z') c = (char)(c - 32);   // +a types A
							int vk = c is >= 'a' and <= 'z' ? c - 32 : c is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ' ? c : 0;
							bool shift = c is >= 'A' and <= 'Z' && (held & Keys.Shift) == 0;
							if (shift) { held |= Keys.Shift; Mods(); }
							try
							{
								if ((held & Keys.Alt) != 0)
								{
									if (vk != 0) InjectSysKeyDown(vk);
									InjectSysChar(c);
								}
								else
								{
									bool taken = vk != 0 && InjectKeyDown(vk);
									if (!taken)
										InjectChar((held & Keys.Control) != 0 && vk is >= 'A' and <= 'Z' ? (char)(vk - '@') : c);
								}
								if (vk != 0) InjectKeyUp(vk);
							}
							finally
							{
								if (shift) { held &= ~Keys.Shift; Mods(); }
							}
							continue;
						}
					}
				}
			}
			finally { SetModifierKeys(before); }
			return sent;
		}

		// ---- the beep ------------------------------------------------------------------------

		/// <summary>The system's alert sound, from the host (NSBeep on macOS).</summary>
		internal static Action SystemBeep;

		internal override void AudibleAlert(AlertType alert)
		{
			try { SystemBeep?.Invoke(); }
			catch (Exception) { }
		}

		// ---- the power supply ----------------------------------------------------------------

		/// <summary>
		/// GetSystemPowerStatus, as WinForms reports it: the charge (High above 66 %, Low below 33 %, Critical
		/// below 5 %, Charging while it charges, NoSystemBattery without one), the percentage, the seconds left
		/// (-1 unknown) and whether the machine is on mains power. From IOKit's power sources on macOS and from
		/// /sys/class/power_supply on Linux; elsewhere mains power and no battery.
		/// </summary>
		internal override PowerStatus PowerStatus
		{
			get
			{
				try
				{
					if (OperatingSystem.IsWindows() && WindowsPower() is PowerStatus win) return win;
					if (OperatingSystem.IsMacOS() && MacPower() is PowerStatus mac) return mac;
					if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() && LinuxPower() is PowerStatus linux) return linux;
				}
				catch (Exception) { }
				return new PowerStatus(BatteryChargeStatus.NoSystemBattery, -1, 1f, -1, PowerLineStatus.Online);
			}
		}

		private static PowerStatus Make(bool hasBattery, int percent, bool charging, int secondsLeft, bool? onMains)
		{
			PowerLineStatus line = onMains is bool m ? (m ? PowerLineStatus.Online : PowerLineStatus.Offline) : PowerLineStatus.Unknown;
			if (!hasBattery) return new PowerStatus(BatteryChargeStatus.NoSystemBattery, -1, 1f, -1, line);
			BatteryChargeStatus charge = percent > 66 ? BatteryChargeStatus.High : percent < 33 ? BatteryChargeStatus.Low : 0;
			if (percent < 5) charge |= BatteryChargeStatus.Critical;
			if (charging) charge |= BatteryChargeStatus.Charging;
			return new PowerStatus(charge, -1, Math.Clamp(percent, 0, 100) / 100f, secondsLeft, line);
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SYSTEM_POWER_STATUS
		{
			public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
			public int BatteryLifeTime, BatteryFullLifeTime;
		}

		[DllImport("kernel32")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

		private static PowerStatus WindowsPower()
		{
			if (!GetSystemPowerStatus(out SYSTEM_POWER_STATUS p)) return null;
			return new PowerStatus((BatteryChargeStatus)p.BatteryFlag, p.BatteryFullLifeTime,
				p.BatteryLifePercent == 255 ? 1f : p.BatteryLifePercent / 100f, p.BatteryLifeTime, (PowerLineStatus)p.ACLineStatus);
		}

		private static PowerStatus LinuxPower()
		{
			const string root = "/sys/class/power_supply";
			if (!Directory.Exists(root)) return null;
			bool? mains = null;
			bool battery = false, charging = false;
			int percent = 100, secondsLeft = -1;
			static string Read(string dir, string name)
			{
				string p = Path.Combine(dir, name);
				return File.Exists(p) ? File.ReadAllText(p).Trim() : null;
			}
			foreach (string dir in Directory.GetDirectories(root))
			{
				string type = Read(dir, "type");
				if (type == "Mains" || type == "USB")
				{
					if (Read(dir, "online") is string online) mains = (mains ?? false) || online == "1";
				}
				else if (type == "Battery" && Read(dir, "scope") != "Device")   // a mouse's battery is not the machine's
				{
					battery = true;
					if (int.TryParse(Read(dir, "capacity"), out int c)) percent = c;
					string status = Read(dir, "status");
					charging = status == "Charging";
					if (status == "Discharging" && int.TryParse(Read(dir, "time_to_empty_now"), out int t) && t > 0) secondsLeft = t;
					if (mains == null && status != null) mains = status != "Discharging";
				}
			}
			return Make(battery, percent, charging, secondsLeft, mains);
		}

		private static PowerStatus MacPower()
		{
			IntPtr info = IOPSCopyPowerSourcesInfo();
			if (info == IntPtr.Zero) return null;
			try
			{
				bool? mains = null;
				IntPtr providing = IOPSGetProvidingPowerSourceType(info);
				if (providing != IntPtr.Zero) mains = CFString(providing) != "Battery Power";
				IntPtr list = IOPSCopyPowerSourcesList(info);
				if (list == IntPtr.Zero) return Make(false, 100, false, -1, mains);
				try
				{
					long n = CFArrayGetCount(list);
					for (long i = 0; i < n; i++)
					{
						IntPtr d = IOPSGetPowerSourceDescription(info, CFArrayGetValueAtIndex(list, i));
						if (d == IntPtr.Zero || DictString(d, "Type") != "InternalBattery") continue;
						int current = DictInt(d, "Current Capacity") ?? 100, max = DictInt(d, "Max Capacity") ?? 100;
						int percent = max > 0 ? (int)Math.Round(current * 100.0 / max) : current;
						bool charging = DictBool(d, "Is Charging") ?? false;
						int minutes = DictInt(d, "Time to Empty") ?? -1;
						return Make(true, percent, charging, mains == false && minutes > 0 ? minutes * 60 : -1, mains);
					}
					return Make(false, 100, false, -1, mains);
				}
				finally { CFRelease(list); }
			}
			finally { CFRelease(info); }
		}

		private static IntPtr DictValue(IntPtr dict, string key)
		{
			IntPtr k = CFStringCreateWithCString(IntPtr.Zero, key, 0x08000100 /* kCFStringEncodingUTF8 */);
			try { return CFDictionaryGetValue(dict, k); }
			finally { CFRelease(k); }
		}

		private static int? DictInt(IntPtr dict, string key)
		{
			IntPtr v = DictValue(dict, key);
			return v != IntPtr.Zero && CFNumberGetValue(v, 9 /* kCFNumberIntType */, out int n) ? n : null;
		}

		private static bool? DictBool(IntPtr dict, string key)
		{
			IntPtr v = DictValue(dict, key);
			return v == IntPtr.Zero ? null : CFBooleanGetValue(v);
		}

		private static string DictString(IntPtr dict, string key)
		{
			IntPtr v = DictValue(dict, key);
			return v == IntPtr.Zero ? null : CFString(v);
		}

		private static string CFString(IntPtr s)
		{
			var buffer = new byte[256];
			return CFStringGetCString(s, buffer, buffer.Length, 0x08000100) ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is int z && z >= 0 ? z : buffer.Length) : null;
		}

		private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
		private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
		[DllImport(IOKit)] private static extern IntPtr IOPSCopyPowerSourcesInfo();
		[DllImport(IOKit)] private static extern IntPtr IOPSCopyPowerSourcesList(IntPtr blob);
		[DllImport(IOKit)] private static extern IntPtr IOPSGetPowerSourceDescription(IntPtr blob, IntPtr ps);
		[DllImport(IOKit)] private static extern IntPtr IOPSGetProvidingPowerSourceType(IntPtr blob);
		[DllImport(CoreFoundation)] private static extern long CFArrayGetCount(IntPtr array);
		[DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
		[DllImport(CoreFoundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
		[DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out int value);
		[DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr boolean);
		[DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
		[DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string s, uint encoding);
		[DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
	}
}
