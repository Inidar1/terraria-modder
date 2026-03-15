using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Audio;
using Terraria.GameContent.UI.Elements;
using Terraria.Localization;
using Terraria.UI;
using Terraria.UI.Gamepad;
namespace TerrariaModder.Core.UI
{
    internal abstract class NativeModsStateBase : UIState, IHaveBackButtonCommand
    {
        protected readonly NativeModsService Service;
        protected readonly UIState PreviousState;
        protected readonly bool InGame;
        protected UIList List;
        protected UIPanel Panel;
        protected UIElement Root;
        protected UIScrollbar Scrollbar;
        protected UITextPanel<string> TitlePanel;
        protected UITextPanel<LocalizedText> BackPanel;

        protected NativeModsStateBase(NativeModsService service, UIState previousState, bool inGame)
        {
            Service = service;
            PreviousState = previousState;
            InGame = inGame;
        }

        public override void OnInitialize()
        {
            Root = new UIElement();
            Root.Width.Set(0f, 0.86f);
            Root.MaxWidth.Set(900f, 0f);
            Root.Height.Set(-140f, 1f);
            Root.HAlign = 0.5f;
            Root.VAlign = 0.5f;

            Panel = new UIPanel();
            Panel.Width.Set(0f, 1f);
            Panel.Height.Set(-110f, 1f);
            Panel.BackgroundColor = new Color(33, 43, 79) * 0.8f;
            Root.Append(Panel);

            List = new UIList();
            List.Width.Set(-30f, 1f);
            List.Height.Set(-20f, 1f);
            List.Top.Set(10f, 0f);
            List.ListPadding = 6f;
            Panel.Append(List);

            Scrollbar = new UIScrollbar();
            Scrollbar.SetView(100f, 1000f);
            Scrollbar.Height.Set(-20f, 1f);
            Scrollbar.HAlign = 1f;
            Scrollbar.Top.Set(10f, 0f);
            Panel.Append(Scrollbar);
            List.SetScrollbar(Scrollbar);

            TitlePanel = new UITextPanel<string>(GetTitle(), 0.8f, large: true);
            TitlePanel.HAlign = 0.5f;
            TitlePanel.Top.Set(-42f, 0f);
            TitlePanel.SetPadding(15f);
            TitlePanel.BackgroundColor = new Color(73, 94, 171);
            Root.Append(TitlePanel);

            BackPanel = new UITextPanel<LocalizedText>(Language.GetText("UI.Back"), 0.7f, large: true);
            BackPanel.Width.Set(-10f, 0.5f);
            BackPanel.Height.Set(50f, 0f);
            BackPanel.VAlign = 1f;
            BackPanel.Top.Set(-45f, 0f);
            BackPanel.OnMouseOver += FadedMouseOver;
            BackPanel.OnMouseOut += FadedMouseOut;
            BackPanel.OnLeftClick += (_, __) => GoBack();
            BackPanel.SetSnapPoint("Back", 0);
            Root.Append(BackPanel);

            Append(Root);
            RebuildList();
        }

        protected abstract string GetTitle();
        protected abstract void RebuildList();
        protected virtual bool CanGoBack() => true;

        protected UITextPanel<string> CreateButton(string text, System.Action onClick, float height = 40f)
        {
            var button = new UITextPanel<string>(text, 0.72f, large: false);
            button.Width.Set(0f, 1f);
            button.Height.Set(height, 0f);
            button.SetPadding(12f);
            button.BackgroundColor = new Color(63, 82, 151) * 0.7f;
            button.OnMouseOver += FadedMouseOver;
            button.OnMouseOut += FadedMouseOut;
            button.OnLeftClick += (_, __) => onClick();
            return button;
        }

        protected UITextPanel<string> CreateInfoRow(string text, float height = 34f)
        {
            var row = new UITextPanel<string>(text, 0.65f, large: false);
            row.Width.Set(0f, 1f);
            row.Height.Set(height, 0f);
            row.SetPadding(10f);
            row.BackgroundColor = new Color(24, 31, 57) * 0.7f;
            row.BorderColor = new Color(55, 66, 114);
            return row;
        }

        protected void SetStatus(string text)
        {
            _ = text;
        }

        protected float GetScrollPosition()
        {
            return Scrollbar?.ViewPosition ?? 0f;
        }

        protected void RestoreScrollPosition(float viewPosition)
        {
            if (Scrollbar == null)
                return;

            List?.Recalculate();
            Scrollbar.ViewPosition = viewPosition;
        }

        protected void BringToFront(UIElement element)
        {
            if (element == null || element.Parent == null)
                return;

            UIElement parent = element.Parent;
            parent.RemoveChild(element);
            parent.Append(element);
        }

        protected void GoBack()
        {
            SoundEngine.PlaySound(11);
            if (InGame)
            {
                if (PreviousState != null)
                    Main.InGameUI.SetState(PreviousState);
                else
                    IngameFancyUI.Close(quiet: true);
                return;
            }

            if (PreviousState != null)
            {
                Main.menuMode = 888;
                Main.MenuUI.SetState(PreviousState);
            }
            else
            {
                Main.menuMode = 0;
                Main.MenuUI.SetState(null);
            }
        }

        protected static void FadedMouseOver(UIMouseEvent evt, UIElement listeningElement)
        {
            SoundEngine.PlaySound(12);
            if (evt.Target is UIPanel panel)
            {
                panel.BackgroundColor = new Color(73, 94, 171);
                panel.BorderColor = Color.White;
            }
        }

        protected static void FadedMouseOut(UIMouseEvent evt, UIElement listeningElement)
        {
            if (evt.Target is UIPanel panel)
            {
                panel.BackgroundColor = new Color(63, 82, 151) * 0.7f;
                panel.BorderColor = Color.Black;
            }
        }

        protected override void DrawSelf(SpriteBatch spriteBatch)
        {
            base.DrawSelf(spriteBatch);
            UILinkPointNavigator.Shortcuts.BackButtonCommand = 7;
        }

        public void HandleBackButtonUsage()
        {
            if (CanGoBack())
                GoBack();
        }
    }
}
