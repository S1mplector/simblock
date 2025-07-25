using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using SimBlock.Core.Domain.Entities;
using SimBlock.Core.Domain.Enums;
using SimBlock.Core.Domain.Interfaces;
using SimBlock.Presentation.Configuration;

namespace SimBlock.Infrastructure.Windows
{
    /// <summary>
    /// Windows-specific implementation of keyboard hook service using Win32 API
    /// </summary>
    public class WindowsKeyboardHookService : IKeyboardHookService
    {
        private readonly ILogger<WindowsKeyboardHookService> _logger;
        private readonly UISettings _uiSettings;
        private readonly KeyboardBlockState _state;
        private IntPtr _hookId = IntPtr.Zero;
        private NativeMethods.LowLevelKeyboardProc _proc;

        // Emergency unlock tracking
        private int _emergencyUnlockCount = 0;
        private DateTime _lastEmergencyKeyPress = DateTime.MinValue;
        private const int EMERGENCY_UNLOCK_REQUIRED_PRESSES = 3;
        private const int EMERGENCY_UNLOCK_TIMEOUT_MS = 2000; // 2 seconds between presses
        
        // Track modifier key states within the hook
        private bool _ctrlPressed = false;
        private bool _altPressed = false;
        private bool _shiftPressed = false;

        public event EventHandler<KeyboardBlockState>? BlockStateChanged;
        public event EventHandler<int>? EmergencyUnlockAttempt;

        public bool IsHookInstalled => _hookId != IntPtr.Zero;
        public KeyboardBlockState CurrentState => _state;

        public WindowsKeyboardHookService(ILogger<WindowsKeyboardHookService> logger, UISettings uiSettings)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _uiSettings = uiSettings ?? throw new ArgumentNullException(nameof(uiSettings));
            _state = new KeyboardBlockState();
            _proc = HookCallback;
        }

        public Task InstallHookAsync()
        {
            if (_hookId != IntPtr.Zero)
            {
                _logger.LogWarning("Hook is already installed");
                return Task.CompletedTask;
            }

            _logger.LogInformation("Installing keyboard hook...");

            // For low-level keyboard hooks (WH_KEYBOARD_LL) the hMod parameter MUST be NULL (IntPtr.Zero)
            _hookId = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _proc,
                IntPtr.Zero,
                0);

            if (_hookId == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                _logger.LogError("Failed to install keyboard hook. Error code: {ErrorCode}", error);
                throw new InvalidOperationException($"Failed to install keyboard hook. Error code: {error}");
            }

            _logger.LogInformation("Keyboard hook installed successfully");

            return Task.CompletedTask;
        }

        public Task UninstallHookAsync()
        {
            if (_hookId == IntPtr.Zero)
            {
                _logger.LogWarning("Hook is not installed");
                return Task.CompletedTask;
            }

            _logger.LogInformation("Uninstalling keyboard hook...");

            bool result = NativeMethods.UnhookWindowsHookEx(_hookId);
            if (!result)
            {
                int error = Marshal.GetLastWin32Error();
                _logger.LogError("Failed to uninstall keyboard hook. Error code: {ErrorCode}", error);
            }
            else
            {
                _logger.LogInformation("Keyboard hook uninstalled successfully");
            }

            _hookId = IntPtr.Zero;

            return Task.CompletedTask;
        }

        public Task SetBlockingAsync(bool shouldBlock, string? reason = null)
        {
            return Task.Run(() =>
            {
                _logger.LogInformation("Setting keyboard blocking to {ShouldBlock}. Reason: {Reason}", 
                    shouldBlock, reason ?? "Not specified");

                _state.SetBlocked(shouldBlock, reason);
                BlockStateChanged?.Invoke(this, _state);
            });
        }

        public Task ToggleBlockingAsync(string? reason = null)
        {
            return Task.Run(() =>
            {
                _logger.LogInformation("Toggling keyboard blocking. Current state: {CurrentState}. Reason: {Reason}",
                    _state.IsBlocked, reason ?? "Not specified");

                _state.Toggle(reason);
                BlockStateChanged?.Invoke(this, _state);
            });
        }
        
        /// <summary>
        /// Sets the keyboard blocking to simple mode
        /// </summary>
        public Task SetSimpleModeAsync(string? reason = null)
        {
            return Task.Run(() =>
            {
                _logger.LogInformation("Setting keyboard blocking to simple mode. Reason: {Reason}",
                    reason ?? "Not specified");

                _state.SetSimpleMode(reason);
                BlockStateChanged?.Invoke(this, _state);
            });
        }
        
        /// <summary>
        /// Sets the keyboard blocking to advanced mode with specific configuration
        /// </summary>
        public Task SetAdvancedModeAsync(AdvancedKeyboardConfiguration config, string? reason = null)
        {
            return Task.Run(() =>
            {
                _logger.LogInformation("Setting keyboard blocking to advanced mode. Reason: {Reason}",
                    reason ?? "Not specified");

                _state.SetAdvancedMode(config, reason);
                BlockStateChanged?.Invoke(this, _state);
            });
        }
        
        /// <summary>
        /// Sets the keyboard blocking to select mode with specific configuration
        /// </summary>
        public Task SetSelectModeAsync(AdvancedKeyboardConfiguration config, string? reason = null)
        {
            return Task.Run(() =>
            {
                _logger.LogInformation("Setting keyboard blocking to select mode. Reason: {Reason}",
                    reason ?? "Not specified");

                _state.SetSelectMode(config, reason);
                BlockStateChanged?.Invoke(this, _state);
            });
        }
        
        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kbStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                int message = wParam.ToInt32();
                
                // Log all key events when debugging emergency unlock
                if ((Keys)kbStruct.vkCode == Keys.U ||
                    (Keys)kbStruct.vkCode == Keys.ControlKey ||
                    (Keys)kbStruct.vkCode == Keys.Menu)
                {
                    _logger.LogInformation("Keyboard hook received key: {Key} (vkCode: {VkCode}), Message: {Message}, IsBlocked: {IsBlocked}, Mode: {Mode}",
                        (Keys)kbStruct.vkCode, kbStruct.vkCode, message, _state.IsBlocked, _state.Mode);
                }
                
                // Track modifier key states
                TrackModifierKeys(kbStruct.vkCode, message);
                
                // Always check for emergency unlock combination (works for both keyboard and mouse blocking)
                // Only trigger on key down events (WM_KEYDOWN = 0x0100)
                if (message == NativeMethods.WM_KEYDOWN && IsEmergencyUnlockCombination(kbStruct.vkCode))
                {
                    _logger.LogInformation("Emergency unlock combination detected in keyboard hook");
                    HandleEmergencyUnlock();
                    // If keyboard is blocked, prevent this key from reaching applications
                    if (_state.IsBlocked)
                    {
                        return (IntPtr)1; // Block this key press to prevent it from reaching applications
                    }
                    // If only mouse is blocked, allow the key to pass through but still handle emergency unlock
                }
                
                // Check if we should block the key using the new advanced blocking logic
                if (_state.IsKeyBlocked((Keys)kbStruct.vkCode))
                {
                    // Block the key based on current mode and configuration
                    _logger.LogDebug("Blocking keyboard input for key: {Key} (Mode: {Mode})",
                        (Keys)kbStruct.vkCode, _state.Mode);
                    return (IntPtr)1; // Return non-zero to suppress the key
                }
            }

            // Allow the key to pass through
            return NativeMethods.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private void HandleEmergencyUnlock()
        {
            try
            {
                var now = DateTime.Now;
                var timeSinceLastPress = now - _lastEmergencyKeyPress;

                // Reset counter if too much time has passed
                if (timeSinceLastPress.TotalMilliseconds > EMERGENCY_UNLOCK_TIMEOUT_MS)
                {
                    _emergencyUnlockCount = 0;
                }

                _emergencyUnlockCount++;
                _lastEmergencyKeyPress = now;

                _logger.LogInformation("Emergency unlock attempt {Count}/{Required}",
                    _emergencyUnlockCount, EMERGENCY_UNLOCK_REQUIRED_PRESSES);

                // Notify UI about emergency unlock attempt
                EmergencyUnlockAttempt?.Invoke(this, _emergencyUnlockCount);

                // Check if we've reached the required number of presses
                if (_emergencyUnlockCount >= EMERGENCY_UNLOCK_REQUIRED_PRESSES)
                {
                    _logger.LogWarning("Emergency unlock activated! Keyboard will be unlocked.");
                    
                    // Reset counter
                    _emergencyUnlockCount = 0;
                    
                    // Unlock the keyboard
                    _ = SetBlockingAsync(false, "Emergency unlock (3x Ctrl+Alt+U)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during emergency unlock");
            }
        }

        private void TrackModifierKeys(uint vkCode, int message)
        {
            try
            {
                bool isKeyDown = message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN;
                bool isKeyUp = message == NativeMethods.WM_KEYUP || message == NativeMethods.WM_SYSKEYUP;
                
                // Track Control key state
                if (vkCode == NativeMethods.VK_LCONTROL || vkCode == NativeMethods.VK_RCONTROL || vkCode == NativeMethods.VK_CONTROL)
                {
                    if (isKeyDown)
                        _ctrlPressed = true;
                    else if (isKeyUp)
                        _ctrlPressed = false;
                }
                
                // Track Alt key state
                if (vkCode == NativeMethods.VK_LMENU || vkCode == NativeMethods.VK_RMENU || vkCode == NativeMethods.VK_MENU)
                {
                    if (isKeyDown)
                        _altPressed = true;
                    else if (isKeyUp)
                        _altPressed = false;
                }
                
                // Track Shift key state
                if (vkCode == NativeMethods.VK_LSHIFT || vkCode == NativeMethods.VK_RSHIFT || vkCode == NativeMethods.VK_SHIFT)
                {
                    if (isKeyDown)
                        _shiftPressed = true;
                    else if (isKeyUp)
                        _shiftPressed = false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error tracking modifier keys");
            }
        }

        private bool IsEmergencyUnlockCombination(uint vkCode)
        {
            try
            {
                // Convert Keys enum to virtual key code
                uint configuredKeyCode = (uint)_uiSettings.EmergencyUnlockKey;
                
                // Check if it's the configured emergency unlock key
                if (vkCode == configuredKeyCode)
                {
                    // Check if the required modifiers are pressed
                    bool ctrlMatch = !_uiSettings.EmergencyUnlockRequiresCtrl || _ctrlPressed;
                    bool altMatch = !_uiSettings.EmergencyUnlockRequiresAlt || _altPressed;
                    bool shiftMatch = !_uiSettings.EmergencyUnlockRequiresShift || _shiftPressed;
                    
                    // Ensure at least one modifier is required and pressed
                    // Fix: Check if ANY required modifier is being used, not just if it's pressed
                    bool hasAnyRequiredModifier = _uiSettings.EmergencyUnlockRequiresCtrl ||
                                                 _uiSettings.EmergencyUnlockRequiresAlt ||
                                                 _uiSettings.EmergencyUnlockRequiresShift;
                    
                    bool hasRequiredModifiers = !hasAnyRequiredModifier || // If no modifiers required, always true
                                               (_uiSettings.EmergencyUnlockRequiresCtrl && _ctrlPressed) ||
                                               (_uiSettings.EmergencyUnlockRequiresAlt && _altPressed) ||
                                               (_uiSettings.EmergencyUnlockRequiresShift && _shiftPressed);
                    
                    // Debug logging
                    _logger.LogInformation("Emergency unlock key check - Key: {Key} (vkCode: {VkCode}, configuredCode: {ConfiguredCode}), " +
                        "Ctrl: {CtrlPressed}/{CtrlRequired}, Alt: {AltPressed}/{AltRequired}, Shift: {ShiftPressed}/{ShiftRequired}, " +
                        "HasRequiredModifiers: {HasMods}",
                        _uiSettings.EmergencyUnlockKey, vkCode, configuredKeyCode,
                        _ctrlPressed, _uiSettings.EmergencyUnlockRequiresCtrl,
                        _altPressed, _uiSettings.EmergencyUnlockRequiresAlt,
                        _shiftPressed, _uiSettings.EmergencyUnlockRequiresShift,
                        hasRequiredModifiers);
                    
                    bool result = ctrlMatch && altMatch && shiftMatch && hasRequiredModifiers;
                    _logger.LogInformation("Emergency unlock combination check result: {Result}", result);
                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking emergency unlock combination");
            }

            return false;
        }
    }
}
