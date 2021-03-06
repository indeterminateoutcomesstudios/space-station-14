﻿using System;
using System.Collections.Generic;
using System.Linq;
using SS14.Client.Graphics;
using SS14.Client.Graphics.Clyde;
using SS14.Client.Graphics.Drawing;
using SS14.Client.Input;
using SS14.Client.Interfaces;
using SS14.Client.Interfaces.Graphics;
using SS14.Client.Interfaces.Input;
using SS14.Client.Interfaces.UserInterface;
using SS14.Client.UserInterface.Controls;
using SS14.Client.UserInterface.CustomControls;
using SS14.Client.Utility;
using SS14.Shared.Configuration;
using SS14.Shared.Input;
using SS14.Shared.Interfaces.Configuration;
using SS14.Shared.IoC;
using SS14.Shared.Maths;

namespace SS14.Client.UserInterface
{
    internal sealed class UserInterfaceManager : IPostInjectInit, IDisposable, IUserInterfaceManagerInternal
    {
        [Dependency] private readonly IConfigurationManager _config;
        [Dependency] private readonly ISceneTreeHolder _sceneTreeHolder;
        [Dependency] private readonly IInputManager _inputManager;
        [Dependency] private readonly IDisplayManager _displayManager;

        public UITheme ThemeDefaults { get; private set; }
        public Stylesheet Stylesheet { get; set; }
        public Control KeyboardFocused { get; private set; }

        // When a control receives a mouse down it must also receive a mouse up and mouse moves, always.
        // So we keep track of which control is "focused" by the mouse.
        private Control _mouseFocused;

        private Godot.CanvasLayer CanvasLayer;
        public Control StateRoot { get; private set; }
        public Control CurrentlyHovered { get; private set; }
        public Control RootControl { get; private set; }
        public Control WindowRoot { get; private set; }
        public AcceptDialog PopupControl { get; private set; }
        public DebugConsole DebugConsole { get; private set; }
        public IDebugMonitors DebugMonitors => _debugMonitors;
        private DebugMonitors _debugMonitors;

        public Dictionary<(GodotAsset asset, int resourceId), object> GodotResourceInstanceCache { get; } =
            new Dictionary<(GodotAsset asset, int resourceId), object>();

        public void PostInject()
        {
            _config.RegisterCVar("key.keyboard.console", Keyboard.Key.Tilde, CVar.ARCHIVE);
        }

        public void Initialize()
        {
            ThemeDefaults = new UIThemeDefault();

            _initializeCommon();

            DebugConsole = new DebugConsole();
            RootControl.AddChild(DebugConsole);

            _debugMonitors = new DebugMonitors();
            RootControl.AddChild(_debugMonitors);

            _inputManager.SetInputCommand(EngineKeyFunctions.ShowDebugMonitors,
                InputCmdHandler.FromDelegate(enabled: session => { DebugMonitors.Visible = true; },
                    disabled: session => { DebugMonitors.Visible = false; }));
        }

        private void _initializeCommon()
        {
            if (GameController.OnGodot)
            {
                CanvasLayer = new Godot.CanvasLayer
                {
                    Name = "UILayer",
                    Layer = CanvasLayers.LAYER_GUI
                };

                _sceneTreeHolder.SceneTree.GetRoot().AddChild(CanvasLayer);
            }

            RootControl = new Control("UIRoot")
            {
                MouseFilter = Control.MouseFilterMode.Ignore
            };
            RootControl.SetAnchorPreset(Control.LayoutPreset.Wide);
            if (!GameController.OnGodot)
            {
                RootControl.Size = _displayManager.ScreenSize;
                _displayManager.OnWindowResized += args => RootControl.Size = args.NewSize;
            }

            if (GameController.OnGodot)
            {
                CanvasLayer.AddChild(RootControl.SceneControl);
            }

            StateRoot = new Control("StateRoot")
            {
                MouseFilter = Control.MouseFilterMode.Ignore
            };
            StateRoot.SetAnchorPreset(Control.LayoutPreset.Wide);
            RootControl.AddChild(StateRoot);

            WindowRoot = new Control("WindowRoot")
            {
                MouseFilter = Control.MouseFilterMode.Ignore
            };
            WindowRoot.SetAnchorPreset(Control.LayoutPreset.Wide);
            RootControl.AddChild(WindowRoot);

            PopupControl = new AcceptDialog("RootPopup")
            {
                Resizable = true
            };
            RootControl.AddChild(PopupControl);
        }

        public void InitializeTesting()
        {
            ThemeDefaults = new UIThemeDummy();

            _initializeCommon();
        }

        public void Dispose()
        {
            RootControl.Dispose();
        }

        public void Update(ProcessFrameEventArgs args)
        {
            RootControl.DoUpdate(args);
        }

        public void FrameUpdate(RenderFrameEventArgs args)
        {
            RootControl.DoFrameUpdate(args);
        }

        public void MouseDown(MouseButtonEventArgs args)
        {
            var control = MouseGetControl(args.Position);
            if (control == null)
            {
                return;
            }

            _mouseFocused = control;

            if (_mouseFocused.CanKeyboardFocus && _mouseFocused.KeyboardFocusOnClick)
            {
                _mouseFocused.GrabKeyboardFocus();
            }

            var guiArgs = new GUIMouseButtonEventArgs(args.Button, args.DoubleClick, control, Mouse.ButtonMask.None,
                args.Position, args.Position - control.GlobalPosition, args.Alt, args.Control, args.Shift,
                args.System);

            _doMouseGuiInput(control, guiArgs, (c, ev) => c.MouseDown(ev));

            // Always mark this as handled.
            // The only case it should not be is if we do not have a control to click on,
            // in which case we never reach this.
            args.Handle();
        }

        public void MouseUp(MouseButtonEventArgs args)
        {
            if (_mouseFocused == null)
            {
                return;
            }

            var guiArgs = new GUIMouseButtonEventArgs(args.Button, args.DoubleClick, _mouseFocused,
                Mouse.ButtonMask.None,
                args.Position, args.Position - _mouseFocused.GlobalPosition, args.Alt, args.Control, args.Shift,
                args.System);

            _doMouseGuiInput(_mouseFocused, guiArgs, (c, ev) => c.MouseUp(ev));
            _mouseFocused = null;

            // Always mark this as handled.
            // The only case it should not be is if we do not have a control to click on,
            // in which case we never reach this.
            args.Handle();
        }

        public void MouseMove(MouseMoveEventArgs mouseMoveEventArgs)
        {
            // Update which control is considered hovered.
            var newHovered = _mouseFocused ?? MouseGetControl(mouseMoveEventArgs.Position);
            if (newHovered != CurrentlyHovered)
            {
                CurrentlyHovered?.MouseExited();
                CurrentlyHovered = newHovered;
                CurrentlyHovered?.MouseEntered();
            }

            var target = _mouseFocused ?? newHovered;
            if (target != null)
            {
                var guiArgs = new GUIMouseMoveEventArgs(mouseMoveEventArgs.Relative, mouseMoveEventArgs.Speed,
                    target,
                    mouseMoveEventArgs.ButtonMask, mouseMoveEventArgs.Position,
                    mouseMoveEventArgs.Position - target.GlobalPosition, mouseMoveEventArgs.Alt,
                    mouseMoveEventArgs.Control, mouseMoveEventArgs.Shift, mouseMoveEventArgs.System);

                _doMouseGuiInput(target, guiArgs, (c, ev) => c.MouseMove(ev));
            }
        }

        public void MouseWheel(MouseWheelEventArgs args)
        {
            var control = MouseGetControl(args.Position);
            if (control == null)
            {
                return;
            }

            args.Handle();

            var guiArgs = new GUIMouseWheelEventArgs(args.WheelDirection, control, Mouse.ButtonMask.None, args.Position,
                args.Position - control.GlobalPosition, args.Alt, args.Control, args.Shift, args.System);

            _doMouseGuiInput(control, guiArgs, (c, ev) => c.MouseWheel(ev), true);
        }

        public void TextEntered(TextEventArgs textEvent)
        {
            if (KeyboardFocused == null)
            {
                return;
            }

            var guiArgs = new GUITextEventArgs(KeyboardFocused, textEvent.CodePoint);
            KeyboardFocused.TextEntered(guiArgs);
        }

        public void KeyDown(KeyEventArgs keyEvent)
        {
            // TODO: This is ugly.
            if (keyEvent.Key == Keyboard.Key.Tilde)
            {
                keyEvent.Handle();
                DebugConsole.Toggle();
                return;
            }

            if (KeyboardFocused == null)
            {
                return;
            }

            var guiArgs = new GUIKeyEventArgs(KeyboardFocused, keyEvent.Key, keyEvent.IsRepeat, keyEvent.Alt,
                keyEvent.Control, keyEvent.Shift, keyEvent.System);
            KeyboardFocused.KeyDown(guiArgs);

            if (guiArgs.Handled)
            {
                keyEvent.Handle();
            }
        }

        public void KeyUp(KeyEventArgs keyEvent)
        {
            if (KeyboardFocused == null)
            {
                return;
            }

            var guiArgs = new GUIKeyEventArgs(KeyboardFocused, keyEvent.Key, keyEvent.IsRepeat, keyEvent.Alt,
                keyEvent.Control, keyEvent.Shift, keyEvent.System);
            KeyboardFocused.KeyUp(guiArgs);
        }

        public void DisposeAllComponents()
        {
            RootControl.DisposeAllChildren();
        }

        public void Popup(string contents, string title = "Alert!")
        {
            PopupControl.DialogText = contents;
            PopupControl.Title = title;
            PopupControl.OpenMinimum();
        }

        public Control MouseGetControl(Vector2 coordinates)
        {
            return _mouseFindControlAtPos(RootControl, coordinates);
        }

        public void GrabKeyboardFocus(Control control)
        {
            if (control == null)
            {
                throw new ArgumentNullException(nameof(control));
            }

            if (!control.CanKeyboardFocus)
            {
                throw new ArgumentException("Control cannot get keyboard focus.", nameof(control));
            }

            if (control == KeyboardFocused)
            {
                return;
            }

            ReleaseKeyboardFocus();

            KeyboardFocused = control;

            KeyboardFocused.FocusEntered();
        }

        public void ReleaseKeyboardFocus()
        {
            var oldFocused = KeyboardFocused;
            oldFocused?.FocusExited();
            KeyboardFocused = null;
        }

        public void ReleaseKeyboardFocus(Control ifControl)
        {
            if (ifControl == null)
            {
                throw new ArgumentNullException(nameof(ifControl));
            }

            if (ifControl == KeyboardFocused)
            {
                ReleaseKeyboardFocus();
            }
        }

        public void GDPreKeyDown(KeyEventArgs args)
        {
            if (args.Key == Keyboard.Key.Quote)
            {
                DebugConsole.Toggle();
                args.Handle();
            }
        }

        public void GDPreKeyUp(KeyEventArgs args)
        {
        }

        public void Render(IRenderHandle renderHandle)
        {
            var drawHandle = renderHandle.CreateHandleScreen();

            _render(drawHandle, RootControl, Vector2.Zero, Color.White, null);
        }

        private static void _render(DrawingHandleScreen handle, Control control, Vector2 position, Color modulate,
            UIBox2i? scissorBox)
        {
            if (!control.Visible)
            {
                return;
            }

            handle.SetTransform(position, Angle.Zero, Vector2.One);
            handle.Modulate = modulate * control.ActualModulateSelf;
            var clip = control.RectClipContent;
            var scissorRegion = scissorBox;
            if (clip)
            {
                scissorRegion = UIBox2i.FromDimensions((Vector2i)position, (Vector2i)control.Size);
                if (scissorBox != null)
                {
                    // Make the final scissor region a sub region of scissorBox
                    var s = scissorBox.Value;
                    var result = s.Intersection(scissorRegion.Value);
                    if (result == null)
                    {
                        // Uhm... No intersection so... don't draw anything at all?
                        return;
                    }

                    scissorRegion = result.Value;
                }

                handle.SetScissor(scissorRegion);
            }
            control.Draw(handle);
            foreach (var child in control.Children)
            {
                _render(handle, child, position + child.Position.Rounded(), modulate, scissorRegion);
            }

            if (clip)
            {
                handle.SetScissor(scissorBox);
            }
        }

        public void GDUnhandledMouseDown(MouseButtonEventArgs args)
        {
            KeyboardFocused?.ReleaseKeyboardFocus();
        }

        public void GDUnhandledMouseUp(MouseButtonEventArgs args)
        {
            //throw new System.NotImplementedException();
        }

        public void GDFocusEntered(Control control)
        {
            KeyboardFocused = control;
        }

        public void GDFocusExited(Control control)
        {
            if (KeyboardFocused == control)
            {
                KeyboardFocused = null;
            }
        }

        public void GDMouseEntered(Control control)
        {
            CurrentlyHovered = control;
        }

        public void GDMouseExited(Control control)
        {
            if (control == CurrentlyHovered)
            {
                CurrentlyHovered = null;
            }
        }

        private Control _mouseFindControlAtPos(Control control, Vector2 position)
        {
            foreach (var child in control.Children.Reverse())
            {
                if (!child.Visible)
                {
                    continue;
                }

                var maybeFoundOnChild = _mouseFindControlAtPos(child, position - child.Position);
                if (maybeFoundOnChild != null)
                {
                    return maybeFoundOnChild;
                }
            }

            if (control.MouseFilter != Control.MouseFilterMode.Ignore && control.HasPoint(position))
            {
                return control;
            }

            return null;
        }

        private void _doMouseGuiInput<T>(Control control, T guiEvent, Action<Control, T> action, bool ignoreStop=false)
            where T : GUIMouseEventArgs
        {
            while (control != null)
            {
                if (control.MouseFilter != Control.MouseFilterMode.Ignore)
                {
                    action(control, guiEvent);

                    if (guiEvent.Handled || (!ignoreStop && control.MouseFilter == Control.MouseFilterMode.Stop))
                    {
                        break;
                    }
                }

                guiEvent.RelativePosition += control.Position;
                control = control.Parent;
                guiEvent.SourceControl = control;
            }
        }
    }
}
