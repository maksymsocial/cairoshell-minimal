using CairoDesktop.AppGrabber;
using CairoDesktop.Application.Interfaces;
using CairoDesktop.Common;
using CairoDesktop.Infrastructure.ObjectModel;
using CairoDesktop.Infrastructure.Services;
using CairoDesktop.Taskbar.SupportingClasses;
using ManagedShell.AppBar;
using ManagedShell.WindowsTasks;

namespace CairoDesktop.Taskbar.Services
{
    public class TaskbarWindowService : AppBarWindowService
    {
        private readonly IAppGrabber _appGrabber;
        private readonly IDesktopManager _desktopManager;
        private readonly ICommandService _commandService;

        private bool tasksInitialized;

        public TaskbarWindowService(ICairoApplication cairoApplication,
            AppBarEventService appBarEventService,
            ShellManagerService shellManagerService,
            IWindowManager windowManager,
            IDesktopManager desktopManager, 
            IAppGrabber appGrabber,
            ICommandService commandService,
            Settings settings) 
            : base(cairoApplication, appBarEventService, shellManagerService, windowManager, settings)
        {
            _appGrabber = appGrabber;
            _desktopManager = desktopManager;
            _commandService = commandService;

            EnableMultiMon = _settings.EnableTaskbarMultiMon;
            EnableService = _settings.EnableTaskbar;

            _appBarEventService.AppBarEvent += AppBarEvent;

            if (EnableService)
            {
                initTasks();
                _shellManager.ExplorerHelper.HideExplorerTaskbar = true;
            }
        }

        protected override void HandleSettingChange(string setting)
        {
            switch (setting)
            {
                case "EnableTaskbar":
                    if (_settings.EnableTaskbar)
                    {
                        initTasks();
                    }
                    _shellManager.ExplorerHelper.HideExplorerTaskbar = _settings.EnableTaskbar;

                    HandleEnableServiceChanged(_settings.EnableTaskbar);
                    break;
                case "EnableTaskbarMultiMon":
                    HandleEnableMultiMonChanged(_settings.EnableTaskbarMultiMon);
                    break;
                case "TaskbarGroupingStyle":
                    _shellManager.Tasks.SetTaskCategoryProvider(getTaskCategoryProvider());
                    break;
                case "TaskbarStyle":
                    recreateWindows();
                    break;
                case "TaskbarEdge":
                case "MinTaskbarSlideAway":
                case "MinTaskbarReserveSpace":
                case "MinTaskbarFade":
                    // the minimal taskbar picks these up when it is created
                    if (_settings.TaskbarStyle == 1) recreateWindows();
                    break;
            }
        }

        private void recreateWindows()
        {
            if (!EnableService)
            {
                return;
            }

            HandleEnableServiceChanged(false);
            HandleEnableServiceChanged(true);
        }

        private void AppBarEvent(object sender, AppBarEventArgs e)
        {
            bool hides = _settings.TaskbarStyle == 1 ? (_settings.MinTaskbarSlideAway || _settings.MinTaskbarFade) : _settings.TaskbarMode == 2;

            if (!EnableService || !hides || sender is Taskbar || sender is MinimalTaskbar)
            {
                return;
            }

            if (!(sender is CairoAppBarWindow otherBar))
            {
                return;
            }

            var window = _windowManager.GetScreenWindow(Windows, otherBar.Screen);

            if (window == null || window.AppBarEdge != otherBar.AppBarEdge)
            {
                return;
            }

            bool disable;
            if (e.Reason == AppBarEventReason.MouseEnter) disable = true;
            else if (e.Reason == AppBarEventReason.MouseLeave) disable = false;
            else return;

            if (window is Taskbar taskbar) taskbar.DisableAutoHide = disable;
            else if (window is MinimalTaskbar minimalTaskbar) minimalTaskbar.DisableAutoHide = disable;
        }

        protected override void OpenWindow(AppBarScreen screen)
        {
            CairoAppBarWindow newTaskbar;

            if (_settings.TaskbarStyle == 1)
            {
                newTaskbar = new MinimalTaskbar(_cairoApplication, _shellManager, _windowManager, _desktopManager, _appGrabber, _commandService, _settings, screen, _settings.TaskbarEdge);
            }
            else
            {
                newTaskbar = new Taskbar(_cairoApplication, _shellManager, _windowManager, _desktopManager, _appGrabber, _commandService, _settings, screen, _settings.TaskbarEdge, _settings.TaskbarMode);
            }

            Windows.Add(newTaskbar);
            newTaskbar.Show();
        }

        public override void Dispose()
        {
            base.Dispose();

            _appBarEventService.AppBarEvent -= AppBarEvent;

            if (EnableService)
            {
                _shellManager.ExplorerHelper.HideExplorerTaskbar = false;
            }
        }

        private void initTasks()
        {
            if (tasksInitialized)
            {
                return;
            }

            _shellManager.Tasks.Initialize(getTaskCategoryProvider(), true);
            tasksInitialized = true;
        }

        private ITaskCategoryProvider getTaskCategoryProvider()
        {
            if (_settings.TaskbarGroupingStyle == 0)
            {
                return new AppGrabberTaskCategoryProvider(_appGrabber, _shellManager);
            }
            else
            {
                return new ApplicationTaskCategoryProvider();
            }
        }
    }
}
