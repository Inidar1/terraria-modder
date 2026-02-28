using System;
using TerrariaModder.Core.Logging;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;
using StorageHub.Crafting;
using StorageHub.Config;
using StorageHub.UI.Tabs;

namespace StorageHub.UI
{
    /// <summary>
    /// Standalone recipes browser opened from keybind.
    /// Keeps the main Storage Hub UI controller-gated.
    /// </summary>
    public class RecipeBrowserUI
    {
        public const string PanelId = "storage-hub-recipes";

        private readonly ILogger _log;
        private readonly RecipesTab _recipesTab;

        private bool _isOpen;
        private int _framesSinceRefresh;

        private const int PanelWidth = 980;
        private const int PanelHeight = 640;
        private const int HeaderHeight = 35;
        private const int ContentPadding = 10;
        private const int RefreshIntervalFrames = 30;

        private int _panelX = -1;
        private int _panelY = -1;
        private bool _isDragging;
        private int _dragOffsetX;
        private int _dragOffsetY;

        public bool IsOpen => _isOpen;

        public RecipeBrowserUI(ILogger log, RecipeIndex recipeIndex, CraftabilityChecker checker, StorageHubConfig config)
        {
            _log = log;
            _recipesTab = new RecipesTab(log, recipeIndex, checker, config);
        }

        public void Toggle()
        {
            if (_isOpen)
            {
                CloseWithEscape();
                return;
            }

            Open();
        }

        public void Open()
        {
            _isOpen = true;
            _framesSinceRefresh = 0;
            _recipesTab.MarkDirty();

            int x = _panelX >= 0 ? _panelX : (UIRenderer.ScreenWidth - PanelWidth) / 2;
            int y = _panelY >= 0 ? _panelY : (UIRenderer.ScreenHeight - PanelHeight) / 2;
            UIRenderer.RegisterPanelBounds(PanelId, x, y, PanelWidth, PanelHeight);
            UIRenderer.OpenInventory();
        }

        public void CloseWithEscape()
        {
            if (!_isOpen)
                return;

            _isOpen = false;
            UIRenderer.UnregisterPanelBounds(PanelId);
            UIRenderer.CloseInventory();
        }

        public void Close()
        {
            if (!_isOpen)
                return;

            _isOpen = false;
            UIRenderer.UnregisterPanelBounds(PanelId);
        }

        public void MarkDirty()
        {
            _recipesTab.MarkDirty();
            _framesSinceRefresh = 0;
        }

        public void Update()
        {
            if (!_isOpen)
                return;

            _framesSinceRefresh++;
            if (_framesSinceRefresh >= RefreshIntervalFrames)
            {
                _recipesTab.MarkDirty();
                _framesSinceRefresh = 0;
            }

            _recipesTab.Update();
        }

        public void Draw()
        {
            if (!_isOpen)
                return;

            if (InputState.IsKeyJustPressed(TerrariaModder.Core.Input.KeyCode.Escape))
            {
                CloseWithEscape();
                return;
            }

            bool blockInput = WidgetInput.ShouldBlockForHigherPriorityPanel(PanelId);
            WidgetInput.BlockInput = blockInput;

            try
            {
                if (_panelX < 0) _panelX = (UIRenderer.ScreenWidth - PanelWidth) / 2;
                if (_panelY < 0) _panelY = (UIRenderer.ScreenHeight - PanelHeight) / 2;

                HandleDragging();

                _panelX = Math.Max(0, Math.Min(_panelX, UIRenderer.ScreenWidth - PanelWidth));
                _panelY = Math.Max(0, Math.Min(_panelY, UIRenderer.ScreenHeight - PanelHeight));

                int x = _panelX;
                int y = _panelY;
                UIRenderer.RegisterPanelBounds(PanelId, x, y, PanelWidth, PanelHeight);

                UIRenderer.DrawPanel(x, y, PanelWidth, PanelHeight, UIColors.PanelBg);
                DrawHeader(x, y);

                int contentX = x + ContentPadding;
                int contentY = y + HeaderHeight + ContentPadding;
                int contentWidth = PanelWidth - ContentPadding * 2;
                int contentHeight = PanelHeight - HeaderHeight - ContentPadding * 2;
                _recipesTab.Draw(contentX, contentY, contentWidth, contentHeight);

                if (WidgetInput.IsMouseOver(x, y, PanelWidth, PanelHeight) && !blockInput)
                {
                    if (WidgetInput.MouseLeftClick)
                        WidgetInput.ConsumeClick();
                    if (WidgetInput.MouseRightClick)
                        WidgetInput.ConsumeRightClick();
                    if (WidgetInput.MouseMiddleClick)
                        WidgetInput.ConsumeMiddleClick();
                    if (WidgetInput.ScrollWheel != 0)
                        WidgetInput.ConsumeScroll();
                }
            }
            catch (Exception ex)
            {
                _log.Error($"RecipeBrowserUI Draw error: {ex.Message}");
            }
            finally
            {
                WidgetInput.BlockInput = false;
            }
        }

        private void HandleDragging()
        {
            bool blockInput = WidgetInput.ShouldBlockForHigherPriorityPanel(PanelId);
            bool inHeader = WidgetInput.IsMouseOver(_panelX, _panelY, PanelWidth - 40, HeaderHeight) && !blockInput;

            if (WidgetInput.MouseLeftClick && inHeader && !_isDragging)
            {
                _isDragging = true;
                _dragOffsetX = WidgetInput.MouseX - _panelX;
                _dragOffsetY = WidgetInput.MouseY - _panelY;
                WidgetInput.ConsumeClick();
            }

            if (_isDragging)
            {
                if (WidgetInput.MouseLeft)
                {
                    _panelX = WidgetInput.MouseX - _dragOffsetX;
                    _panelY = WidgetInput.MouseY - _dragOffsetY;
                }
                else
                {
                    _isDragging = false;
                }
            }
        }

        private void DrawHeader(int x, int y)
        {
            UIRenderer.DrawRect(x, y, PanelWidth, HeaderHeight, UIColors.HeaderBg);

            TerrariaModder.Core.PluginLoader.LoadModIcons();
            var icon = TerrariaModder.Core.PluginLoader.GetMod("storage-hub")?.IconTexture ?? TerrariaModder.Core.PluginLoader.DefaultIcon;
            int titleX = x + 10;
            if (icon != null)
            {
                UIRenderer.DrawTexture(icon, x + 8, y + 6, 22, 22);
                titleX = x + 34;
            }

            UIRenderer.DrawTextShadow("Storage Hub Recipes", titleX, y + 8, UIColors.TextTitle);
            UIRenderer.DrawText("Standalone recipe browser", titleX + 175, y + 10, UIColors.TextHint);

            bool blockInput = WidgetInput.ShouldBlockForHigherPriorityPanel(PanelId);
            int closeX = x + PanelWidth - 35;
            bool closeHover = WidgetInput.IsMouseOver(closeX, y + 3, 30, 30) && !blockInput;
            UIRenderer.DrawRect(closeX, y + 3, 30, 30, closeHover ? UIColors.CloseBtnHover : UIColors.CloseBtn);
            UIRenderer.DrawText("X", closeX + 11, y + 10, UIColors.Text);

            if (closeHover && WidgetInput.MouseLeftClick)
            {
                CloseWithEscape();
                WidgetInput.ConsumeClick();
            }
        }
    }
}
