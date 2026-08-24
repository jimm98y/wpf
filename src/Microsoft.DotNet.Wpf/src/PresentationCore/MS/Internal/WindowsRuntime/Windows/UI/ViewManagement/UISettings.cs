// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace MS.Internal.WindowsRuntime
{
    namespace Windows.UI.ViewManagement
    {
        internal class UISettings : IDisposable
        {
            private readonly bool _isSupported;

            private UISettingsRCW.IUISettings3 _uisettings;

            private static readonly Color _fallbackAccentColor = Color.FromArgb(0xff, 0x00, 0x78, 0xd4);

            private static readonly Color s_white = Color.FromArgb(0xff, 0xff, 0xff, 0xff);

            private static readonly Color s_black = Color.FromArgb(0xff, 0x00, 0x00, 0x00);

            private Color _accentColor, _accentLight1, _accentLight2, _accentLight3;
            private Color _accentDark1, _accentDark2, _accentDark3;

            private bool _useFallbackColor = false;

            internal UISettings()
            {
                _isSupported = false;

                try
                {
                    _uisettings = GetWinRTInstance() as UISettingsRCW.IUISettings3;
                }
                catch (COMException)
                {
                    // We don't want to throw any exceptions here.
                    // If we can't get the instance, we will use the default accent color.
                }

                if (_uisettings != null)
                {
                    _isSupported = true;
                    TryUpdateAccentColors();
                }
            }

            /// <summary>
            ///     Gets the accent color value for the desired color type.
            /// </summary>
            /// <returns>
            ///     Always returns a usable color. When the native UISettings provider is
            ///     unavailable (e.g. on non-Windows platforms) or the fetch fails, this shim
            ///     synthesizes a shade of the fallback accent color so that the light/dark
            ///     variations are still visually distinct rather than a single flat color.
            /// </returns>
            internal bool TryGetColorValue(UISettingsRCW.UIColorType desiredColor, out Color color)
            {
                if(_isSupported)
                {
                    try
                    {
                        var uiColor = _uisettings.GetColorValue(desiredColor);
                        color = Color.FromArgb(uiColor.A, uiColor.R, uiColor.G, uiColor.B);
                        return true;
                    }
                    catch (COMException)
                    {
                        // We don't want to throw any exceptions here.
                        // If we can't get the instance, we will use the fallback accent color.
                    }
                }

                // The native WinRT UISettings provider isn't available to hand back the
                // light/dark shade variations, so synthesize them from the fallback accent
                // color instead. Without this every AccentColor*Brush would resolve to the
                // exact same flat color. We report success because the shim has supplied a
                // usable value.
                color = GetFallbackShade(desiredColor);
                return true;
            }

            /// <summary>
            ///   Derives a light or dark shade of <see cref="_fallbackAccentColor"/> for the
            ///   requested accent color type. Light shades blend the accent toward white and dark
            ///   shades blend it toward black, approximating the variations Windows exposes.
            /// </summary>
            private static Color GetFallbackShade(UISettingsRCW.UIColorType desiredColor)
            {
                return desiredColor switch
                {
                    UISettingsRCW.UIColorType.AccentLight1 => Blend(_fallbackAccentColor, s_white, 0.30),
                    UISettingsRCW.UIColorType.AccentLight2 => Blend(_fallbackAccentColor, s_white, 0.50),
                    UISettingsRCW.UIColorType.AccentLight3 => Blend(_fallbackAccentColor, s_white, 0.70),
                    UISettingsRCW.UIColorType.AccentDark1 => Blend(_fallbackAccentColor, s_black, 0.20),
                    UISettingsRCW.UIColorType.AccentDark2 => Blend(_fallbackAccentColor, s_black, 0.40),
                    UISettingsRCW.UIColorType.AccentDark3 => Blend(_fallbackAccentColor, s_black, 0.55),
                    _ => _fallbackAccentColor,
                };
            }

            /// <summary>
            ///   Linearly interpolates between <paramref name="from"/> and <paramref name="to"/> by
            ///   <paramref name="amount"/> (0 returns <paramref name="from"/>, 1 returns <paramref name="to"/>).
            /// </summary>
            private static Color Blend(Color from, Color to, double amount)
            {
                static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

                return Color.FromArgb(
                    from.A,
                    Lerp(from.R, to.R, amount),
                    Lerp(from.G, to.G, amount),
                    Lerp(from.B, to.B, amount));
            }

            /// <summary>
            ///   Tries to update the accent colors properties.
            ///   If any call to TryGetColorValue fails, we set _useFallbackColor to true.
            ///   After which all the accent values will be the default color.
            ///   This is to ensure that we don't have inconsistent set of accent color values stored.
            /// </summary>
            internal void TryUpdateAccentColors()
            {
                _useFallbackColor = true;

                if(_isSupported)
                {
                    try
                    {
                        if(TryGetColorValue(UISettingsRCW.UIColorType.Accent, out Color systemAccent))
                        {
                            // For verifying if any of the calls to TryGetColorValue fails.
                            bool result = true;
                            if(_accentColor != systemAccent)
                            {
                                result &= TryGetColorValue(UISettingsRCW.UIColorType.AccentLight1, out _accentLight1);
                                result &= TryGetColorValue(UISettingsRCW.UIColorType.AccentLight2, out _accentLight2);
                                result &= TryGetColorValue(UISettingsRCW.UIColorType.AccentLight3, out _accentLight3);
                                result &= TryGetColorValue(UISettingsRCW.UIColorType.AccentDark1, out _accentDark1);
                                result &= TryGetColorValue(UISettingsRCW.UIColorType.AccentDark2, out _accentDark2);
                                result &= TryGetColorValue(UISettingsRCW.UIColorType.AccentDark3, out _accentDark3);
                                _accentColor = systemAccent;
                            }
                            // If result is false, hence atleast one call, use fallback values.
                            _useFallbackColor = !result;
                        }
                    }
                    catch
                    {
                        // We don't want to throw any exceptions here.
                        // If we can't get any one of the color values, we will use the default accent color.
                    }
                }
            }

            /// <summary>
            ///   Gets the WinRT instance of UISettings.
            /// </summary>
            private static object GetWinRTInstance()
            {
                object winRtInstance = null;
                try
                {
                    winRtInstance = UISettingsRCW.GetUISettingsInstance();
                }
                catch (Exception e) when (e is TypeLoadException || e is FileNotFoundException)
                {
                    winRtInstance = null;
                }

                return winRtInstance;
            }

            #region Color Properties

            internal Color AccentColor => _useFallbackColor ? _fallbackAccentColor : _accentColor;
            internal Color AccentLight1 => _useFallbackColor ? _fallbackAccentColor : _accentLight1;
            internal Color AccentLight2 => _useFallbackColor ? _fallbackAccentColor : _accentLight2;
            internal Color AccentLight3 => _useFallbackColor ? _fallbackAccentColor : _accentLight3;
            internal Color AccentDark1 => _useFallbackColor ? _fallbackAccentColor : _accentDark1;
            internal Color AccentDark2 => _useFallbackColor ? _fallbackAccentColor : _accentDark2;
            internal Color AccentDark3 => _useFallbackColor ? _fallbackAccentColor : _accentDark3;

            #endregion

            #region IDisposable

            private bool _disposed = false;

            ~UISettings()
            {
                Dispose(false);
            }

            public void Dispose()
            {
                Dispose(true);
                GC.SuppressFinalize(this);
            }

            private void Dispose(bool disposing)
            {
                if (!_disposed)
                {
                    if (_uisettings != null)
                    {
                        try
                        {
                            // Release the _uiSettings instance here
                            Marshal.ReleaseComObject(_uisettings);
                        }
                        catch
                        {
                            // Don't want to raise any exceptions in a finalizer, eat them here
                        }

                        _uisettings = null;
                    }

                    _disposed = true;
                }
            }

            #endregion
        }
    }
}