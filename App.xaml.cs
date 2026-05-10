using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using RoboCopyGUI.Services;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace RoboCopyGUI
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private Window? _window;

        /// <summary>The settings loaded at startup; mutated and saved by MainWindow.</summary>
        public static AppSettings Settings { get; private set; } = new();

        /// <summary>UI thread dispatcher captured when the main window is created;
        /// used by background services (e.g. <c>CopyEngine</c>) to marshal updates.</summary>
        public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; internal set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // Bring up logging + settings before XAML so any constructor-time issues are captured.
            try
            {
                Settings = SettingsService.Load();
                LoggingService.Initialize(Settings.LogLevel);
                NotificationService.Initialize();
                Log.Information("RoboCopyGUI starting. Base dir: {BaseDir}", AppContext.BaseDirectory);
            }
            catch (Exception ex)
            {
                // Never let logging/settings init crash the app launch, but don't swallow
                // silently either — surface to the debugger and to whatever logger may
                // have been created before the failure.
                System.Diagnostics.Debug.WriteLine($"[RoboCopyGUI] Startup init failed: {ex}");
                try { Log.Error(ex, "Startup init failed (logging/settings/notifications)."); }
                catch { /* logger may itself be the culprit */ }
            }

            InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            _window.Closed += (_, _) =>
            {
                try
                {
                    SettingsService.Save(Settings);
                }
                finally
                {
                    NotificationService.Shutdown();
                    LoggingService.Shutdown();
                }
            };
            _window.Activate();
        }
    }
}
