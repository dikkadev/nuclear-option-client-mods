using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NuclearConnectivity
{
    internal static class SteamTimelineBridge
    {
        private const string SteamTimelineInterfaceVersion = "STEAMTIMELINE_INTERFACE_V004";
        private const uint DefaultPriority = 100;

        private static readonly object Sync = new object();

        private static IntPtr _timelinePtr;
        private static bool _initAttempted;
        private static bool _loggedUnavailable;

        private static SetTimelineGameModeDelegate? _setTimelineGameMode;
        private static AddInstantaneousTimelineEventDelegate? _addInstantaneousTimelineEvent;

        internal static void SetGameModePlaying()
        {
            if (!TryInitialize())
            {
                return;
            }

            _setTimelineGameMode!(_timelinePtr, TimelineGameMode.Playing);
        }

        internal static void AddMarker(string title, string description, string icon, TimelineClipPriority clipPriority)
        {
            if (!TryInitialize())
            {
                return;
            }

            using Utf8StringHandle utf8Title = new Utf8StringHandle(title);
            using Utf8StringHandle utf8Description = new Utf8StringHandle(description);
            using Utf8StringHandle utf8Icon = new Utf8StringHandle(icon);

            _addInstantaneousTimelineEvent!(
                _timelinePtr,
                utf8Title.Pointer,
                utf8Description.Pointer,
                utf8Icon.Pointer,
                DefaultPriority,
                0f,
                clipPriority
            );
        }

        private static bool TryInitialize()
        {
            lock (Sync)
            {
                if (_timelinePtr != IntPtr.Zero)
                {
                    return true;
                }

                if (_initAttempted)
                {
                    return false;
                }

                if (!SteamManager.ClientInitialized)
                {
                    return false;
                }

                _initAttempted = true;

                IntPtr steamClient;
                int steamUser;
                int steamPipe;
                try
                {
                    steamClient = SteamClient();
                    steamUser = SteamAPI_GetHSteamUser();
                    steamPipe = SteamAPI_GetHSteamPipe();
                    if (steamClient == IntPtr.Zero || steamUser == 0 || steamPipe == 0)
                    {
                        LogUnavailable("Steam client handles are unavailable.");
                        return false;
                    }
                    _timelinePtr = SteamAPI_ISteamClient_GetISteamGenericInterface(
                        steamClient, steamUser, steamPipe, SteamTimelineInterfaceVersion);
                }
                catch (DllNotFoundException)
                {
                    LogUnavailable("The native Steam API library is unavailable.");
                    return false;
                }
                catch (EntryPointNotFoundException)
                {
                    LogUnavailable("The native Steam API does not expose the required entry points.");
                    return false;
                }
                catch (BadImageFormatException)
                {
                    LogUnavailable("The native Steam API library has an incompatible architecture.");
                    return false;
                }

                if (_timelinePtr == IntPtr.Zero)
                {
                    LogUnavailable($"Steam Timeline interface {SteamTimelineInterfaceVersion} is unavailable. The game's shipped Steam API may be too old.");
                    return false;
                }

                IntPtr vtable = Marshal.ReadIntPtr(_timelinePtr);
                _setTimelineGameMode = GetVirtualFunction<SetTimelineGameModeDelegate>(vtable, 2);
                _addInstantaneousTimelineEvent = GetVirtualFunction<AddInstantaneousTimelineEventDelegate>(vtable, 3);

                Plugin.Instance.Log.LogInfo("Steam Timeline interface acquired.");
                return true;
            }
        }

        private static void LogUnavailable(string reason)
        {
            if (_loggedUnavailable)
            {
                return;
            }

            _loggedUnavailable = true;
            Plugin.Instance.Log.LogWarning($"Steam Timeline disabled: {reason}");
        }

        private static T GetVirtualFunction<T>(IntPtr vtable, int slot) where T : Delegate
        {
            IntPtr functionPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(functionPtr);
        }

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SteamClient")]
        private static extern IntPtr SteamClient();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SteamAPI_GetHSteamUser();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SteamAPI_GetHSteamPipe();

        [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr SteamAPI_ISteamClient_GetISteamGenericInterface(
            IntPtr steamClient,
            int steamUser,
            int steamPipe,
            string version
        );

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void SetTimelineGameModeDelegate(IntPtr self, TimelineGameMode mode);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate ulong AddInstantaneousTimelineEventDelegate(
            IntPtr self,
            IntPtr title,
            IntPtr description,
            IntPtr icon,
            uint priority,
            float startOffsetSeconds,
            TimelineClipPriority clipPriority
        );

        internal enum TimelineGameMode
        {
            Invalid = 0,
            Playing = 1,
            Staging = 2,
            Menus = 3,
            LoadingScreen = 4,
        }

        internal enum TimelineClipPriority
        {
            Invalid = 0,
            None = 1,
            Standard = 2,
            Featured = 3,
        }

        private sealed class Utf8StringHandle : IDisposable
        {
            private bool _disposed;

            internal Utf8StringHandle(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
                Pointer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, Pointer, bytes.Length);
            }

            internal IntPtr Pointer { get; }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                Marshal.FreeHGlobal(Pointer);
                _disposed = true;
            }
        }
    }
}
