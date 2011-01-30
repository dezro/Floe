﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Floe.Net;

namespace Floe.UI
{
	public partial class ChatControl : UserControl, IDisposable
	{
		#region Nested types

		private class CommandException : Exception
		{
			public CommandException(string message)
				: base(message)
			{
			}
		}

		#endregion

		private const double MinNickListWidth = 50.0;

		private LinkedList<string> _history;
		private LinkedListNode<string> _historyNode;
		private LogFileHandle _logFile;
		private ChatLine _markerLine;

		public readonly static DependencyProperty UIBackgroundProperty = DependencyProperty.Register("UIBackground",
			typeof(SolidColorBrush), typeof(ChatControl));
		public SolidColorBrush UIBackground
		{
			get { return (SolidColorBrush)this.GetValue(UIBackgroundProperty); }
			set { this.SetValue(UIBackgroundProperty, value); }
		}

		public ChatControl(ChatContext context)
		{
			_history = new LinkedList<string>();
			this.Nicknames = new ObservableCollection<NicknameItem>();
			this.Context = context;
			this.Header = context.Target == null ? "Server" : context.Target.ToString();

			InitializeComponent();
			this.SubscribeEvents();

			if (!this.IsServer)
			{
				_logFile = App.OpenLogFile(context.Key);
				var logLines = new List<ChatLine>();
				while (_logFile.Buffer.Count > 0)
				{
					var cl = _logFile.Buffer.Dequeue();
					cl.Marker = _logFile.Buffer.Count == 0 ? ChatMarker.OldMarker : ChatMarker.None;
					logLines.Add(cl);
				}
				boxOutput.AppendBulkLines(logLines);
			}

			var state = App.Settings.Current.Windows.States[context.Key];
			if (this.IsChannel)
			{
				this.Write("Join", string.Format("Now talking on {0}", this.Target.Name));
				this.Session.AddHandler(new IrcCodeHandler((e) =>
					{
						if (e.Message.Parameters.Count > 2 &&
							this.Target.Equals(new IrcTarget(e.Message.Parameters[1])))
						{
							_channelModes = e.Message.Parameters[2].ToCharArray().Where((c) => c != '+').ToArray();
							this.SetTitle();
						}
						e.Handled = true;
						return true;
					}, IrcCode.RPL_CHANNELMODEIS));
				this.Session.Mode(this.Target);
				splitter.IsEnabled = true;
				colNickList.MinWidth = MinNickListWidth;
				colNickList.Width = new GridLength(state.NickListWidth);

				var nameHandler = new IrcCodeHandler((e) =>
					{
						if (e.Message.Parameters.Count >= 3)
						{
							var target = new IrcTarget(e.Message.Parameters[e.Message.Parameters.Count - 2]);
							if (this.Target.Equals(target))
							{
								foreach (var nick in e.Message.Parameters[e.Message.Parameters.Count - 1].Split(' '))
								{
									this.AddNick(nick);
								}
							}
						}
						e.Handled = true;
						return false;
					}, IrcCode.RPL_NAMEREPLY);
				this.Session.AddHandler(nameHandler);
				this.Session.AddHandler(new IrcCodeHandler((e) =>
					{
						this.Session.RemoveHandler(nameHandler);
						e.Handled = true;
						return true;
					}, IrcCode.RPL_ENDOFNAMES));
			}
			else if (this.IsNickname)
			{
				_prefix = this.Target.Name;
			}
			boxOutput.ColumnWidth = state.ColumnWidth;

			this.Loaded += new RoutedEventHandler(ChatControl_Loaded);
			this.Unloaded += new RoutedEventHandler(ChatControl_Unloaded);
			this.PrepareContextMenus();
			boxOutput.ContextMenu = this.GetDefaultContextMenu();
		}

		public ChatContext Context { get; private set; }
		public IrcSession Session { get { return this.Context.Session; } }
		public IrcTarget Target { get { return this.Context.Target; } }
		public bool IsServer { get { return this.Target == null; } }
		public bool IsChannel { get { return this.Target != null && this.Target.IsChannel; } }
		public bool IsNickname { get { return this.Target != null && !this.Target.IsChannel; } }
		public string Perform { get; set; }

		public static readonly DependencyProperty HeaderProperty =
			DependencyProperty.Register("Header", typeof(string), typeof(ChatControl));
		public string Header
		{
			get { return (string)this.GetValue(HeaderProperty); }
			set { this.SetValue(HeaderProperty, value); }
		}

		public static readonly DependencyProperty TitleProperty =
			DependencyProperty.Register("Title", typeof(string), typeof(ChatControl));
		public string Title
		{
			get { return (string)this.GetValue(TitleProperty); }
			set { this.SetValue(TitleProperty, value); }
		}

		public static readonly DependencyProperty IsConnectedProperty =
			DependencyProperty.Register("IsConnected", typeof(bool), typeof(ChatControl));
		public bool IsConnected
		{
			get { return (bool)this.GetValue(IsConnectedProperty); }
			set { this.SetValue(IsConnectedProperty, value); }
		}

		public static readonly DependencyProperty SelectedLinkProperty =
			DependencyProperty.Register("SelectedLink", typeof(string), typeof(ChatControl));
		public string SelectedLink
		{
			get { return (string)this.GetValue(SelectedLinkProperty); }
			set { this.SetValue(SelectedLinkProperty, value); }
		}

		public static readonly DependencyProperty NotifyStateProperty =
			DependencyProperty.Register("NotifyState", typeof(NotifyState), typeof(ChatControl));
		public NotifyState NotifyState
		{
			get { return (NotifyState)this.GetValue(NotifyStateProperty); }
			set { this.SetValue(NotifyStateProperty, value); }
		}

		public void Connect(Floe.Configuration.ServerElement server)
		{
			this.Session.AutoReconnect = false;
			this.Perform = server.OnConnect;
			this.Connect(server.Hostname, server.Port, server.IsSecure, server.AutoReconnect, server.Password);
		}

		public void Connect(string server, int port, bool useSsl, bool autoReconnect, string password)
		{
			this.Session.Open(server, port, useSsl,
				!string.IsNullOrEmpty(this.Session.Nickname) ?
					this.Session.Nickname : App.Settings.Current.User.Nickname,
				App.Settings.Current.User.Username,
				App.Settings.Current.User.FullName,
				autoReconnect,
				password,
				App.Settings.Current.User.Invisible);
		}

		private void ParseInput(string text)
		{
			this.Execute(text, (Keyboard.Modifiers & ModifierKeys.Shift) > 0);
		}

		private void Write(string styleKey, int nickHashCode, string nick, string text, bool attn)
		{
			var cl = new ChatLine(styleKey, nickHashCode, nick, text, ChatMarker.None);

			if (_hasDeactivated)
			{
				_hasDeactivated = false;
				if (_markerLine != null)
				{
					_markerLine.Marker &= ~ChatMarker.NewMarker;
				}
				_markerLine = cl;
				cl.Marker = ChatMarker.NewMarker;
			}

			if (attn)
			{
				cl.Marker |= ChatMarker.Attention;
			}

			if (this.VisualParent == null)
			{
				if (this.IsNickname)
				{
					// Activity in PM window
					this.NotifyState = NotifyState.Alert;
				}
				else if (!string.IsNullOrEmpty(nick) && this.NotifyState != NotifyState.Alert)
				{
					// Chat activity in channel
					this.NotifyState = NotifyState.ChatActivity;
				}
				else if (this.NotifyState == NotifyState.None)
				{
					// Other activity in channel / server
					this.NotifyState = NotifyState.NoiseActivity;
				}
			}

			boxOutput.AppendLine(cl);
			if (_logFile != null)
			{
				_logFile.WriteLine(cl);
			}
		}

		private void Write(string styleKey, IrcPeer peer, string text, bool attn)
		{
			this.Write(styleKey, string.Format("{0}@{1}", peer.Username, peer.Hostname).GetHashCode(),
				this.GetNickWithLevel(peer.Nickname), text, attn);
		}

		private void Write(string styleKey, string text)
		{
			this.Write(styleKey, 0, null, text, false);
		}

		private void SetInputText(string text)
		{
			txtInput.Text = text;
			txtInput.SelectionStart = text.Length;
			_nickCandidates = null;
		}

		private void SetTitle()
		{
			string userModes = this.Session.UserModes.Length > 0 ?
				string.Format("+{0}", string.Join("", (from c in this.Session.UserModes select c.ToString()).ToArray())) : "";
			string channelModes = _channelModes.Length > 0 ?
				string.Format("+{0}", string.Join("", (from c in _channelModes select c.ToString()).ToArray())) : "";

			if(this.IsServer)
			{
				if (this.Session.State == IrcSessionState.Disconnected)
				{
					this.Title = string.Format("{0} - Not Connected", App.Product);
				}
				else
				{
					this.Title = string.Format("{0} - {1} ({2}) on {3}", App.Product, this.Session.Nickname,
						userModes, this.Session.NetworkName);
				}
			}
			else if (!this.Target.IsChannel)
			{
				this.Title = string.Format("{0} - {1} ({2}) on {3} - {4}", App.Product, this.Session.Nickname,
					userModes, this.Session.NetworkName, _prefix);
			}
			else if (this.Target.IsChannel)
			{
				this.Title = string.Format("{0} - {1} ({2}) on {3} - {4} ({5}) - {6}", App.Product, this.Session.Nickname,
					userModes, this.Session.NetworkName, this.Target.ToString(), channelModes, _topic);
			}
		}

		private void SubmitInput()
		{
			string text = txtInput.Text;
			txtInput.Clear();
			if (_history.Count == 0 || _history.First.Value != text)
			{
				_history.AddFirst(text);
			}
			while (_history.Count > App.Settings.Current.Buffer.InputHistory)
			{
				_history.RemoveLast();
			}
			_historyNode = null;
			this.ParseInput(text);
		}

		private ContextMenu GetDefaultContextMenu()
		{
			if (this.IsServer)
			{
				var menu = this.Resources["cmServer"] as ContextMenu;
				var item = menu.Items[0] as MenuItem;
				if (item != null)
				{
					item.Items.Refresh();
					item.IsEnabled = item.Items.Count > 0;
				}
				return menu;
			}
			else
			{
				return this.Resources["cmChannel"] as ContextMenu;
			}
		}

		public void Dispose()
		{
			var state = App.Settings.Current.Windows.States[this.Context.Key];
			state.NickListWidth = colNickList.ActualWidth;
			state.ColumnWidth = boxOutput.ColumnWidth;
			this.UnsubscribeEvents();
			if (_logFile != null)
			{
				_logFile.Dispose();
			}
		}
	}
}
