﻿using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RainMeadow
{
    // Static/singleton class for online features and callbacks
    // is a mainloopprocess so update bound to game update? worth it? idk
    public class OnlineManager : MainLoopProcess {

        public static string CLIENT_KEY = "client";
        public static string CLIENT_VAL = "Meadow_" + RainMeadow.MeadowVersionStr;
        public static string NAME_KEY = "name";
        public static OnlineManager instance;
        public static Serializer serializer = new Serializer(16000);

        public static Lobby lobby;
        public static CSteamID me;
        public static OnlinePlayer mePlayer;
        public static List<OnlinePlayer> players;
        public static List<Subscription> subscriptions = new();
        //public static Dictionary<string, WorldSession> worldSessions;
        //private static Dictionary<string, RoomSession> roomSessions;

        public OnlineManager(ProcessManager manager) : base(manager, RainMeadow.Ext_ProcessID.OnlineManager)
        {
            instance = this;
            me = SteamUser.GetSteamID();
            mePlayer = new OnlinePlayer(me) { isMe = true, name = SteamFriends.GetPersonaName() };

            framesPerSecond = 20; // alternatively, run as fast as we can for the receiving stuff, but send on a lower tickrate?

            players = new List<OnlinePlayer>() { mePlayer };

            RainMeadow.Debug("OnlineManager Created");
        }

        public override void Update()
        {
            base.Update();
            if(lobby != null)
            {
                if (lobby.isAvailable && !lobby.isActive) lobby.Activate();

                mePlayer.tick++;

                // Stuff mePlayer set to itself, events from the distributed lease system
                //mePlayer.recentlyAckedEvents.Clear();
                //while(mePlayer.OutgoingEvents.Count > 0)
                //{
                //    var e = mePlayer.OutgoingEvents.Dequeue();
                //    mePlayer.recentlyAckedEvents.Add(e);
                //    e.Process();
                //}

                // Incoming messages
                ReceiveData();

                // Prepare outgoing messages
                foreach (var subscription in subscriptions)
                {
                    subscription.Update(mePlayer.tick);
                }

                // Outgoing messages
                foreach (var player in players)
                {
                    SendData(player);
                }
            }
        }

        // Process all incoming messages
        internal void ReceiveData()
        {
            lock (serializer)
            {
                int n;
                IntPtr[] messages = new IntPtr[32];
                do // process in batches
                {
                    n = SteamNetworkingMessages.ReceiveMessagesOnChannel(0, messages, messages.Length);
                    for (int i = 0; i < n; i++)
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(messages[i]);
                        try
                        {
                            var fromPlayer = PlayerFromId(message.m_identityPeer.GetSteamID());
                            if (fromPlayer == null)
                            {
                                RainMeadow.Error("player not found: " + message.m_identityPeer + " " + message.m_identityPeer.GetSteamID());
                                continue;
                            }
                            //RainMeadow.Debug($"Receiving message from {fromPlayer}");
                            Marshal.Copy(message.m_pData, serializer.buffer, 0, message.m_cbSize);
                            serializer.BeginRead(fromPlayer);

                            serializer.PlayerHeaders();
                            if (serializer.Aborted)
                            {
                                RainMeadow.Debug("skipped packet");
                                continue;
                            }

                            int ne = serializer.BeginReadEvents();
                            //RainMeadow.Debug($"Receiving {ne} events");
                            for (int ie = 0; ie < ne; ie++)
                            {
                                ProcessIncomingEvent(serializer.ReadEvent(), fromPlayer);
                            }

                            int ns = serializer.BeginReadStates();
                            //RainMeadow.Debug($"Receiving {ns} states");
                            for (int ist = 0; ist < ns; ist++)
                            {
                                ProcessIncomingState(serializer.ReadState(), fromPlayer);
                            }

                            serializer.EndRead();
                        }
                        catch (Exception e)
                        {
                            RainMeadow.Error("Error reading packet from player : " + message.m_identityPeer.GetSteamID());
                            RainMeadow.Error(e);
                            //throw;
                        }
                        finally
                        {
                            SteamNetworkingMessage_t.Release(messages[i]);
                        }
                    }
                }
                while (n > 0);
                serializer.Free();
            }
        }

        private void ProcessIncomingEvent(PlayerEvent playerEvent, OnlinePlayer fromPlayer)
        {
            //RainMeadow.Debug($"Got event {playerEvent.eventId}:{playerEvent.eventType} from {fromPlayer}");
            fromPlayer.needsAck = true;
            if (IsNewer(playerEvent.eventId, fromPlayer.lastEventFromRemote))
            {
                RainMeadow.Debug($"New event {playerEvent.eventId} - {playerEvent.eventType} from {fromPlayer}, processing...");
                fromPlayer.lastEventFromRemote = playerEvent.eventId;
                playerEvent.Process();
            }
        }

        public static bool IsNewer(ulong eventId, ulong lastIncomingEvent)
        {
            var delta = eventId - lastIncomingEvent;
            return delta != 0 && delta < ulong.MaxValue / 2;
        }
        public static bool IsNewerOrEqual(ulong eventId, ulong lastIncomingEvent)
        {
            var delta = eventId - lastIncomingEvent;
            return delta < ulong.MaxValue / 2;
        }

        private void ProcessIncomingState(ResourceState resourceState, OnlinePlayer fromPlayer)
        {
            resourceState.resource.ReadState(resourceState, fromPlayer.tick);
        }

        internal void SendData(OnlinePlayer toPlayer)
        {
            if(toPlayer.needsAck || toPlayer.OutgoingEvents.Any() || toPlayer.OutgoingStates.Any())
            {
                //RainMeadow.Debug($"Sending message to {toPlayer}");
                lock (serializer)
                {
                    serializer.BeginWrite(toPlayer);

                    serializer.PlayerHeaders();

                    serializer.BeginWriteEvents();
                    //RainMeadow.Debug($"Writing {toPlayer.OutgoingEvents.Count} events");
                    foreach (var e in toPlayer.OutgoingEvents)
                    {
                        if (!serializer.CanFit(e)) throw new IOException("no buffer space for events");
                        serializer.WriteEvent(e);
                    }
                    serializer.EndWriteEvents();

                    serializer.BeginWriteStates();
                    //RainMeadow.Debug($"Writing {toPlayer.OutgoingStates.Count} states");
                    while (toPlayer.OutgoingStates.Count > 0 && serializer.CanFit(toPlayer.OutgoingStates.Peek()))
                    {
                        var s = toPlayer.OutgoingStates.Dequeue();
                        serializer.WriteState(s);
                    }
                    // todo handle states overflow, planing a packet for maximum size and least stale states
                    serializer.EndWriteStates();

                    serializer.EndWrite();

                    unsafe
                    {
                        fixed (byte* ptr = serializer.buffer)
                        {
                            SteamNetworkingMessages.SendMessageToUser(ref toPlayer.oid, (IntPtr)ptr, (uint)serializer.Position, Constants.k_nSteamNetworkingSend_UnreliableNoDelay, 0);
                        }
                    }

                    serializer.Free();
                }
            }
        }

        internal static Subscription AddSubscription(OnlineResource onlineResource, OnlinePlayer player)
        {
            var sub = new Subscription(onlineResource, player);
            subscriptions.Add(sub);
            return sub;
        }

        internal static void RemoveSubscription(Subscription sub)
        {
            subscriptions.Remove(sub);
        }

        internal static void RemoveSubscriptions(OnlineResource onlineResource)
        {
            subscriptions.RemoveAll(s => s.onlineResource == onlineResource);
        }

        internal static OnlineResource ResourceFromIdentifier(string rid)
        {
            if (rid == ".") return lobby;
            if (rid.Length == 2 && lobby.worldSessions.TryGetValue(rid, out var r)) return r;
            if (rid.Length > 2 && lobby.worldSessions.TryGetValue(rid.Substring(0, 2), out var r2) && r2.roomSessions.TryGetValue(rid.Substring(2), out var room)) return room;

            RainMeadow.Error("resource not found : " + rid);
            return null;
        }

        internal static OnlinePlayer PlayerFromId(CSteamID id)
        {
            return players.FirstOrDefault(p => p.id == id);
        }
    }
}
