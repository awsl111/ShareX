﻿#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2015 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

namespace ShareX.IRCLib
{
    public class IRC : IDisposable
    {
        public event Action<MessageInfo> Output;
        public event Action Connected, Disconnected;
        public delegate void MessageEventHandler(UserInfo user, string channel, string message);
        public event MessageEventHandler Message;
        public event Action<UserInfo> WhoisResult;
        public delegate void UserEventHandler(UserInfo user, string channel);
        public event UserEventHandler UserJoined, UserLeft, UserQuit;
        public delegate void UserNickChangedEventHandler(UserInfo user, string newNick);
        public event UserNickChangedEventHandler UserNickChanged;
        public delegate void UserKickedEventHandler(UserInfo user, string channel, string kickedNick);
        public event UserKickedEventHandler UserKicked;

        public IRCInfo Info { get; private set; }
        public bool IsConnected { get; private set; }
        public string LastChannel { get; private set; }

        private TcpClient tcp;
        private StreamReader streamReader;
        private StreamWriter streamWriter;
        private bool disconnecting;
        private List<UserInfo> userList = new List<UserInfo>();

        public IRC()
        {
            Info = new IRCInfo();
        }

        public IRC(IRCInfo info)
        {
            Info = info;
        }

        public IRC(string server, int port, string nickname, string username, string realname, bool invisible)
        {
            Info = new IRCInfo()
            {
                Server = server,
                Port = port,
                Nickname = nickname,
                Username = username,
                Realname = realname,
                Invisible = invisible
            };
        }

        public void Dispose()
        {
            Disconnect();
        }

        public void Connect()
        {
            if (IsConnected)
            {
                return;
            }

            try
            {
                IsConnected = true;
                disconnecting = false;

                tcp = new TcpClient(Info.Server, Info.Port);
                NetworkStream networkStream = tcp.GetStream();
                streamReader = new StreamReader(networkStream);
                streamWriter = new StreamWriter(networkStream);

                Thread connectionThread = new Thread(ConnectionThread);
                connectionThread.Start();

                if (!string.IsNullOrEmpty(Info.Password))
                {
                    SetPassword(Info.Password);
                }

                SetUser(Info.Username, Info.Realname, Info.Invisible);
                ChangeNickname(Info.Nickname);
            }
            catch (Exception e)
            {
                IsConnected = false;
                Console.WriteLine(e.ToString());
            }
        }

        public void Disconnect()
        {
            try
            {
                if (IsConnected)
                {
                    Quit(Info.QuitReason);
                    disconnecting = true;
                }

                if (streamReader != null) streamReader.Close();
                if (streamWriter != null) streamWriter.Close();
                if (tcp != null) tcp.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        private void Reconnect()
        {
            Disconnect();
            Connect();
        }

        private void ConnectionThread()
        {
            try
            {
                string message;

                while ((message = streamReader.ReadLine()) != null)
                {
                    if (!CheckCommand(message)) break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }

            OnDisconnected();
        }

        public void SendRawMessage(string message)
        {
            streamWriter.WriteLine(message);
            streamWriter.Flush();

            CheckCommand(message);
        }

        private bool CheckCommand(string message)
        {
            MessageInfo messageInfo = MessageInfo.Parse(message);

            //:sendak.freenode.net 375 Jaex :- sendak.freenode.net Message of the Day -
            //:sendak.freenode.net 372 Jaex :- Welcome to sendak.freenode.net in Vilnius, Lithuania, EU.
            if ((messageInfo.Command == "375" || messageInfo.Command == "372") && Info.SuppressMOTD)
            {
                return true;
            }

            if (messageInfo.User.UserType == IRCUserType.Me)
            {
                messageInfo.User.Nickname = Info.Nickname;
            }

            OnOutput(messageInfo);

            switch (messageInfo.Command)
            {
                case "PING": //PING :sendak.freenode.net
                    SendRawMessage("PONG :" + messageInfo.Message);
                    break;
                case "376": //:sendak.freenode.net 376 Jaex :End of /MOTD command.
                    OnConnected();
                    break;
                case "PRIVMSG": //:Jaex!Jaex@unaffiliated/jaex PRIVMSG #ShareX :test
                    CheckMessage(messageInfo);
                    break;
                case "JOIN": //:Jaex!Jaex@unaffiliated/jaex JOIN #ShareX or :Jaex!Jaex@unaffiliated/jaex JOIN :#ShareX
                    if (UserJoined != null) UserJoined(messageInfo.User, messageInfo.Parameters.Count > 0 ? messageInfo.Parameters[0] : messageInfo.Message);
                    break;
                case "PART": //:Jaex!Jaex@unaffiliated/jaex PART #ShareX :"Leaving"
                    if (UserLeft != null) UserLeft(messageInfo.User, messageInfo.Parameters[0]);
                    break;
                case "QUIT": //:Jaex!Jaex@unaffiliated/jaex QUIT :Client Quit
                    if (UserQuit != null) UserQuit(messageInfo.User, null);
                    break;
                case "NICK": //:Jaex!Jaex@unaffiliated/jaex NICK :Jaex2
                    if (UserNickChanged != null) UserNickChanged(messageInfo.User, messageInfo.Message);
                    break;
                case "KICK": //:Jaex!Jaex@unaffiliated/jaex KICK #ShareX Jaex2 :Jaex2
                    if (UserKicked != null) UserKicked(messageInfo.User, messageInfo.Parameters[0], messageInfo.Parameters[1]);
                    if (Info.AutoRejoinOnKick) JoinChannel(messageInfo.Parameters[0]);
                    break;
                case "311": //:sendak.freenode.net 311 Jaex ShareX ~ShareX unaffiliated/sharex * :realname
                case "319": //:sendak.freenode.net 319 Jaex ShareX :@#ShareX @#ShareX_Test
                case "312": //:sendak.freenode.net 312 Jaex ShareX sendak.freenode.net :Vilnius, Lithuania, EU
                case "671": //:sendak.freenode.net 671 Jaex ShareX :is using a secure connection
                case "317": //:sendak.freenode.net 317 Jaex ShareX 39110 1440201914 :seconds idle, signon time
                case "330": //:sendak.freenode.net 330 Jaex ShareX ShareX :is logged in as
                case "318": //:sendak.freenode.net 318 Jaex ShareX :End of /WHOIS list.
                    ParseWHOIS(messageInfo);
                    break;
                case "396": //:sendak.freenode.net 396 Jaex unaffiliated/jaex :is now your hidden host (set by services.)
                    if (Info.AutoJoinWaitIdentify)
                    {
                        AutoJoinChannels();
                    }
                    break;
                case "ERROR":
                    return false;
            }

            return true;
        }

        private void CheckMessage(MessageInfo messageInfo)
        {
            string channel = messageInfo.Parameters[0];

            OnMessage(messageInfo.User, channel, messageInfo.Message);

            if (messageInfo.User.UserType == IRCUserType.User)
            {
                HandleAutoResponse(channel, messageInfo.User.Nickname, messageInfo.Message.ToLowerInvariant());
            }
        }

        private void AutoJoinChannels()
        {
            foreach (string channel in Info.AutoJoinChannels)
            {
                JoinChannel(channel);
            }
        }

        private void OnOutput(MessageInfo messageInfo)
        {
            if (Output != null)
            {
                Output(messageInfo);
            }

            Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {messageInfo.Content}");
        }

        protected void OnConnected()
        {
            IsConnected = true;

            if (Connected != null)
            {
                Connected();
            }

            foreach (string command in Info.ConnectCommands)
            {
                SendRawMessage(command);
            }

            if (!Info.AutoJoinWaitIdentify)
            {
                AutoJoinChannels();
            }
        }

        protected void OnDisconnected()
        {
            IsConnected = false;

            if (Disconnected != null)
            {
                Disconnected();
            }

            if (!disconnecting && Info.AutoReconnect)
            {
                Thread.Sleep(Info.AutoReconnectDelay);
                Reconnect();
            }
        }

        protected void OnMessage(UserInfo user, string channel, string message)
        {
            if (Message != null)
            {
                Message(user, channel, message);
            }
        }

        protected void OnWhoisResult(UserInfo userInfo)
        {
            if (WhoisResult != null)
            {
                WhoisResult(userInfo);
            }
        }

        #region Commands

        // JOIN channel
        public void JoinChannel(string channel)
        {
            if (IsConnected)
            {
                SendRawMessage($"JOIN {channel}");
                LastChannel = channel;
            }
            else
            {
                Info.AutoJoinChannels.Add(channel);
            }
        }

        // TOPIC channel :message
        public void SetTopic(string channel, string message)
        {
            SendRawMessage($"TOPIC {channel} :{message}");
        }

        // NOTICE channel/nick :message
        public void SendNotice(string channelnick, string message)
        {
            SendRawMessage($"NOTICE {channelnick} :{message}");
        }

        // PRIVMSG channel/nick :message
        public void SendMessage(string message, string channel = null)
        {
            if (string.IsNullOrEmpty(channel))
            {
                channel = LastChannel;
            }

            SendRawMessage($"PRIVMSG {channel} :{message}");
        }

        // NICK nick
        public void ChangeNickname(string nick)
        {
            SendRawMessage($"NICK {nick}");
            Info.Nickname = nick;
        }

        // PART channel :reason
        public void LeaveChannel(string channel, string reason = null)
        {
            SendRawMessage(AddReason($"PART {channel}", reason));
        }

        // KICK nick :reason
        public void KickUser(string channel, string nick, string reason = null)
        {
            SendRawMessage(AddReason($"KICK {channel} {nick}", reason));
        }

        // PASS password
        public void SetPassword(string password)
        {
            SendRawMessage($"PASS {password}");
        }

        // USER username invisible * :realname
        public void SetUser(string username, string realname, bool invisible)
        {
            SendRawMessage($"USER {username} {(invisible ? 8 : 0)} * :{realname}");
        }

        // WHOIS nick
        public void Whois(string nick, bool detailed = true)
        {
            string msg = $"WHOIS {nick}";
            if (detailed) msg += $" {nick}";
            SendRawMessage(msg);
        }

        // QUIT :reason
        public void Quit(string reason = null)
        {
            SendRawMessage(AddReason("QUIT", reason));
        }

        #endregion Commands

        private string AddReason(string command, string reason)
        {
            if (!string.IsNullOrEmpty(reason)) command += " :" + reason;
            return command;
        }

        private bool HandleAutoResponse(string channel, string nick, string message)
        {
            if (Info.AutoResponse && nick != Info.Nickname)
            {
                foreach (AutoResponseInfo autoResponseInfo in Info.AutoResponseList)
                {
                    if (autoResponseInfo.CheckLastMatchTimer(Info.AutoResponseDelay) && autoResponseInfo.IsMatch(message, nick, Info.Nickname))
                    {
                        // Is it whisper?
                        if (!channel.StartsWith("#"))
                        {
                            channel = nick;
                        }

                        string response = autoResponseInfo.RandomResponse(nick, Info.Nickname);
                        SendMessage(response, channel);

                        return true;
                    }
                }
            }

            return false;
        }

        private void ParseWHOIS(MessageInfo messageInfo)
        {
            UserInfo userInfo;

            switch (messageInfo.Command)
            {
                case "311": //:sendak.freenode.net 311 Jaex ShareX ~ShareX unaffiliated/sharex * :realname
                    if (messageInfo.Parameters.Count >= 4)
                    {
                        userInfo = new UserInfo();
                        userInfo.Nickname = messageInfo.Parameters[1];

                        if (messageInfo.Parameters[2][1] == '~')
                        {
                            userInfo.Username = messageInfo.Parameters[2].Substring(1);
                        }
                        else // Ident
                        {
                            userInfo.Username = messageInfo.Parameters[2];
                        }

                        userInfo.Host = messageInfo.Parameters[3];
                        userInfo.Realname = messageInfo.Message;

                        userList.Add(userInfo);
                    }
                    break;
                case "319": //:sendak.freenode.net 319 Jaex ShareX :@#ShareX @#ShareX_Test
                    if (messageInfo.Parameters.Count >= 2)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.Channels.Clear();
                            userInfo.Channels.AddRange(messageInfo.Message.Split());
                        }
                    }
                    break;
                case "312": //:sendak.freenode.net 312 Jaex ShareX sendak.freenode.net :Vilnius, Lithuania, EU
                    if (messageInfo.Parameters.Count >= 3)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.Server = messageInfo.Parameters[2];
                        }
                    }
                    break;
                case "671": //:sendak.freenode.net 671 Jaex ShareX :is using a secure connection
                    if (messageInfo.Parameters.Count >= 2)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.SecureConnection = true;
                        }
                    }
                    break;
                case "317": //:sendak.freenode.net 317 Jaex ShareX 39110 1440201914 :seconds idle, signon time
                    if (messageInfo.Parameters.Count >= 4)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            int idleTime;
                            if (int.TryParse(messageInfo.Parameters[2], out idleTime))
                            {
                                userInfo.IdleTime = TimeSpan.FromSeconds(idleTime);
                            }

                            int signOnTime;
                            if (int.TryParse(messageInfo.Parameters[3], out signOnTime))
                            {
                                userInfo.SignOnDate = Helpers.UnixToDateTime(signOnTime).ToLocalTime();
                            }
                        }
                    }
                    break;
                case "330": //:sendak.freenode.net 330 Jaex ShareX ShareX :is logged in as
                    if (messageInfo.Parameters.Count >= 3)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.Identity = messageInfo.Parameters[2];
                        }
                    }
                    break;
                case "318": //:sendak.freenode.net 318 Jaex ShareX :End of /WHOIS list.
                    if (messageInfo.Parameters.Count >= 2)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userList.Remove(userInfo);
                            OnWhoisResult(userInfo);
                        }
                    }
                    break;
            }
        }

        private UserInfo FindUser(string nickname)
        {
            if (!string.IsNullOrEmpty(nickname))
            {
                return userList.FirstOrDefault(user => user.Nickname == nickname);
            }

            return null;
        }
    }
}