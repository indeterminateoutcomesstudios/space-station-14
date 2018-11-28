﻿using SS14.Client.Console;
using SS14.Client.Interfaces.Console;
using SS14.Client.UserInterface.Controls;
using SS14.Client.Utility;
using SS14.Shared.Console;
using SS14.Shared.IoC;
using SS14.Shared.Maths;
using SS14.Shared.Reflection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SS14.Shared.Utility;

namespace SS14.Client.UserInterface.CustomControls
{
    // Quick note on how thread safety works in here:
    // Messages from other threads are not actually immediately drawn. They're stored in a queue.
    // Every frame OR the next time a message on the main Godot thread comes in, this queue is drained.
    // This keeps thread safety while still making it so messages are ordered how they come in.
    // And also if Update() stops firing due to an exception loop the console will still work.
    // (At least from the main thread, which is what's throwing the exceptions..)
    //
    // Disable reflection so that we won't be looked at for scene translation.
    [Reflect(false)]
    public class DebugConsole : Control, IDebugConsole
    {
        [Dependency] readonly IClientConsole console;

        private bool firstLine = true;
        private LineEdit CommandBar;
        private RichTextLabel Contents;

        public IReadOnlyDictionary<string, IConsoleCommand> Commands => console.Commands;
        private readonly ConcurrentQueue<FormattedMessage> _messageQueue = new ConcurrentQueue<FormattedMessage>();

        private protected override Godot.Control SpawnSceneControl()
        {
            var node = LoadScene("res://Scenes/DebugConsole/DebugConsole.tscn");
            node.Visible = false;
            return node;
        }

        protected override void Initialize()
        {
            IoCManager.InjectDependencies(this);

            CommandBar = GetChild<LineEdit>("CommandBar");
            Contents = GetChild<RichTextLabel>("Contents");
            Contents.ScrollFollowing = true;

            CommandBar.OnTextEntered += CommandEntered;

            console.AddString += (_, args) => AddLine(args.Text, args.Channel, args.Color);
            console.AddFormatted += (_, args) => AddFormattedLine(args.Message);
            console.ClearText += (_, args) => Clear();
        }

        protected override void Update(ProcessFrameEventArgs args)
        {
            base.Update(args);

            _flushQueue();
        }

        public void Toggle()
        {
            var focus = CommandBar.HasFocus();
            Visible = !Visible;
            if (Visible)
            {
                CommandBar.GrabFocus();
            }
            else if (focus)
            {
                // We manually need to call this.
                // See https://github.com/godotengine/godot/pull/15074
                UserInterfaceManager.FocusExited(CommandBar);
            }
        }

        private void CommandEntered(LineEdit.LineEditEventArgs args)
        {
            if (!string.IsNullOrWhiteSpace(args.Text))
            {
                console.ProcessCommand(args.Text);
                CommandBar.Clear();
            }
        }

        public void AddLine(string text, ChatChannel channel, Color color)
        {
            if (!ThreadUtility.IsOnMainThread())
            {
                var formatted = new FormattedMessage(3);
                formatted.PushColor(color);
                formatted.AddText(text);
                formatted.Pop();
                _messageQueue.Enqueue(formatted);
                return;
            }

            _flushQueue();
            if (!firstLine)
            {
                Contents.NewLine();
            }
            else
            {
                firstLine = false;
            }

            Contents.PushColor(color);
            Contents.AddText(text);
            Contents.Pop(); // Pop the color off.
        }

        public void AddLine(string text, Color color)
        {
            AddLine(text, ChatChannel.Default, color);
        }

        public void AddLine(string text)
        {
            AddLine(text, ChatChannel.Default, Color.White);
        }

        public void AddFormattedLine(FormattedMessage message)
        {
            if (!ThreadUtility.IsOnMainThread())
            {
                _messageQueue.Enqueue(message);
                return;
            }

            _addFormattedLineInternal(message);
        }

        public void Clear()
        {
            lock (Contents)
            {
                Contents.Clear();
                firstLine = true;
            }
        }

        private void _addFormattedLineInternal(FormattedMessage message)
        {
            if (!firstLine)
            {
                Contents.NewLine();
            }
            else
            {
                firstLine = false;
            }

            var pushCount = 0;
            foreach (var tag in message.Tags)
            {
                switch (tag)
                {
                    case FormattedMessage.TagText text:
                        Contents.AddText(text.Text);
                        break;
                    case FormattedMessage.TagColor color:
                        Contents.PushColor(color.Color);
                        pushCount++;
                        break;
                    case FormattedMessage.TagPop _:
                        if (pushCount <= 0)
                        {
                            throw new InvalidOperationException();
                        }

                        Contents.Pop();
                        pushCount--;
                        break;
                }
            }

            for (; pushCount > 0; pushCount--)
            {
                Contents.Pop();
            }
        }

        private void _flushQueue()
        {
            DebugTools.Assert(ThreadUtility.IsOnMainThread());

            while (_messageQueue.TryDequeue(out var message))
            {
                _addFormattedLineInternal(message);
            }
        }
    }
}
