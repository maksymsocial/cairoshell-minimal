using CairoDesktop.Application.Interfaces;
using CairoDesktop.Interop;
using Microsoft.Extensions.Logging;
using System;
using ManagedShell.Common;

namespace CairoDesktop.Infrastructure.Services
{
    public class WinSparkleApplicationUpdateService : DisposableObject, IApplicationUpdateService
    {
        private const string UpdateUrl = "https://cairoshell.github.io/appdescriptor.rss";
        private const string AppName = "Cairo Desktop Environment";
        private const string CompanyName = "Cairo Development Team";
        private const bool UseOfficialUpdates = false;

        private readonly ILogger<WinSparkleApplicationUpdateService> _logger;
        private readonly ICairoApplication _app;

        private bool _isInitialized;

        private WinSparkle.win_sparkle_can_shutdown_callback_t _canShutdownDelegate;
        private WinSparkle.win_sparkle_shutdown_request_callback_t _shutdownDelegate;

        public WinSparkleApplicationUpdateService(ILogger<WinSparkleApplicationUpdateService> logger, ICairoApplication app)
        {
            _isInitialized = false;
            _logger = logger;
            _app = app;

            // This fork (minimal taskbar) must not take updates from the official Cairo feed:
            // "updating" would replace it with stock Cairo. Leaving WinSparkle uninitialized
            // makes IsAvailable false, which hides the update options.
            if (!UseOfficialUpdates) return;

            try
            {
                WinSparkle.win_sparkle_set_appcast_url(UpdateUrl);
                WinSparkle.win_sparkle_set_app_details(CompanyName, AppName, _app.AppVersion);

                _canShutdownDelegate = canShutdown;
                _shutdownDelegate = doShutdown;

                WinSparkle.win_sparkle_set_can_shutdown_callback(_canShutdownDelegate);
                WinSparkle.win_sparkle_set_shutdown_request_callback(_shutdownDelegate);
                WinSparkle.win_sparkle_init();

                _isInitialized = true;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to initialize WinSparkle: {e.Message}");
            }
        }

        public bool AutomaticUpdatesEnabled
        {
            get
            {
                if (!_isInitialized) return false;
                int result = 0;
                try
                {
                    result = WinSparkle.win_sparkle_get_automatic_check_for_updates();
                }
                catch (Exception e)
                {
                    _logger.LogError($"Failed to get WinSparkle settings: {e.Message}");
                }
                
                return Convert.ToBoolean(result);
            }

            set
            {
                if (!_isInitialized) return;
                int intValue = Convert.ToInt32(value);

                try
                {
                    WinSparkle.win_sparkle_set_automatic_check_for_updates(intValue);
                }
                catch (Exception e)
                {
                    _logger.LogError($"Failed to set WinSparkle settings: {e.Message}");
                }
            }
        }

        public bool IsAvailable => _isInitialized;

        public void CheckForUpdates()
        {
            if (!_isInitialized) return;
            try
            {
                WinSparkle.win_sparkle_check_update_with_ui();
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to check for updates: {e.Message}");
            }
        }

        private int canShutdown()
        {
            return 1;
        }

        private void doShutdown()
        {
            _app.ExitCairo();
        }

        protected override void DisposeOfUnManagedResources()
        {
            if (!_isInitialized) return;
            try
            {
                WinSparkle.win_sparkle_cleanup();
                _isInitialized = false;
            }
            catch (Exception e)
            {
                _logger.LogError($"Failed to clean up WinSparkle: {e.Message}");
            }
        }
    }
}