﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System.Text;

namespace Oxide.Plugins
{
    [Info("PayNow", "PayNow Services Inc", "0.0.7")]
    internal class PayNow : CovalencePlugin
    {
        const string API_URL = "https://api.paynow.gg/v1/delivery/command-queue/";

        PluginConfig _config;

        readonly Dictionary<string, string> _headers = new Dictionary<string, string>();
        readonly CommandHistory _executedCommands = new CommandHistory(25);
        readonly StringBuilder _cachedStringBuilder = new StringBuilder();
        readonly List<string> _successfulCommandsList = new List<string>(1000);

        #region Oxide

        [HookMethod("Init")]
        void Init()
        {
            _config = Config.ReadObject<PluginConfig>();
            UpdateHeaders();
        }

        [HookMethod("Loaded")]
        void Loaded()
        {
            GetPendingCommands();
            timer.Every(_config.ApiCheckIntervalSeconds, GetPendingCommands);
        }

        [Command("paynow.token")]
        void CommandToken(IPlayer player, string command, string[] args)
        {
            if (!player.IsServer || !player.IsAdmin)
                return;

            if (args.Length != 1)
            {
                player.Reply("Usage: paynow.token <token>");
                return;
            }

            //TODO: Validate token?

            _config.ApiToken = args[0];
            Config.WriteObject(_config, true);

            UpdateHeaders();

            player.Reply("Token set!");
        }

        #endregion

        #region WebRequests

        void GetPendingCommands()
        {
            try
            {
                // Make the API call
                webrequest.Enqueue(API_URL, BuildOnlinePlayersJson(), HandlePendingCommands, this, RequestMethod.POST, _headers);
            }
            catch (Exception ex)
            {
                PrintException("Failed retrieve get pending commands", ex);
            }
        }

        void HandlePendingCommands(int code, string response)
        {
            try
            {
                // Check if we got a valid response
                if (code != 200 || response == null)
                    throw new Exception($"Server sent an invalid response: ({code}) ({response})");

                // Deserialize the response
                QueuedCommand[] data = JsonConvert.DeserializeObject<QueuedCommand[]>(response);
                if (data == null)
                    throw new Exception($"Response deserialized to null: ({response})");

                // Process the data
                ProcessPendingCommands(data);
            }
            catch (Exception ex)
            {
                PrintException("Failed handle pending commands", ex);
            }
        }

        void AcknowledgeCommands(List<string> commandsIds)
        {
            // Check if we have any order ids to acknowledge
            if (commandsIds.Count == 0) return;

            try
            {
                // Make the API call to acknowledge the commands
                webrequest.Enqueue(API_URL, BuildAcknowledgeJson(commandsIds), HandleAcknowledgeCommands, this, RequestMethod.DELETE, _headers);
            }
            catch (Exception ex)
            {
                PrintException("Failed to acknowledge commands", ex);
            }
        }

        void HandleAcknowledgeCommands(int code, string response)
        {
            // Check if we got a valid response
            if (code >= 200 && code < 300) return;

            // Log an error if we didn't get a 204 response
            PrintError($"Command acknowledgement resulted in an unexpected response code: ({code.ToString()}) ({response})");
        }

        #endregion

        #region Command Processing

        void ProcessPendingCommands(QueuedCommand[] queuedCommands)
        {
            // Check if we got any data
            if (queuedCommands.Length == 0)
                return;

            _successfulCommandsList.Clear();
            for (int i = 0; i < queuedCommands.Length; i++)
            {
                QueuedCommand command = queuedCommands[i];

                // Make sure we don't execute the same command twice
                if (_executedCommands.Contains(command.AttemptId))
                    continue;

                try
                {
                    if (command.OnlineOnly && players.Connected.All(x => x.Id != command.SteamId))
                        continue;

                    // Try executing the command
                    if (ExecuteCommand(command.Command))
                    {
                        // Add the order id to the list of acknowledged orders
                        _successfulCommandsList.Add(command.AttemptId);
                        _executedCommands.Add(command.AttemptId);
                    }
                    else
                    {
                        // Log an error if the command failed
                        PrintWarning($"Failed to run command {command.Command} ({command.AttemptId})!");
                    }
                }
                catch (Exception ex)
                {
                    // Log an error if an exception occurs
                    PrintException("Failed to execute command", ex);
                }
            }

            // Log the amount of commands we executed
            Puts($"Received {queuedCommands.Length.ToString()} and executed {_successfulCommandsList.Count.ToString()} commands!");

            // Acknowledge the commands
            AcknowledgeCommands(_successfulCommandsList);
        }

        bool ExecuteCommand(string command)
        {
            // Run the command
            server.Command(command);

            // TODO: Fetch Command Response, currently not possible when using oxide covalence libraries 

            return true;
        }

        #endregion

        #region Api DTOs

        [Serializable]
        public class QueuedCommand
        {
            [JsonProperty("attempt_id")] public string AttemptId;

            [JsonProperty("steam_id")] public string SteamId;

            [JsonProperty("command")] public string Command;

            [JsonProperty("online_only")] public bool OnlineOnly;

            [JsonProperty("queued_at")] public string QueuedAt;
        }

        #endregion

        #region Configuration

        [Serializable]
        class PluginConfig
        {
            public string ApiToken;
            public float ApiCheckIntervalSeconds = 10;
        }

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new PluginConfig(), true);
        }

        #endregion

        #region Helpers

        void UpdateHeaders()
        {
            _headers["Content-Type"] = "application/json";
            _headers["Authorization"] = "Gameserver " + _config.ApiToken;
        }

        string BuildAcknowledgeJson(List<string> orderIds)
        {
            _cachedStringBuilder.Clear();

            // Json format [{"attempt_id": "123"}]
            _cachedStringBuilder.Append("[");
            for (int i = 0; i < orderIds.Count; i++)
            {
                _cachedStringBuilder.Append("{\"attempt_id\": \"");
                _cachedStringBuilder.Append(orderIds[i]);
                _cachedStringBuilder.Append("\"}");

                if (i < orderIds.Count - 1)
                {
                    _cachedStringBuilder.Append(",");
                }
            }

            _cachedStringBuilder.Append("]");

            return _cachedStringBuilder.ToString();
        }

        string BuildOnlinePlayersJson()
        {
            _cachedStringBuilder.Clear();

            // Json format {"steam_ids": ["123"]}
            _cachedStringBuilder.Append("{\"steam_ids\":[");
            var addedPlayers = false;
            foreach (var player in players.Connected)
            {
                addedPlayers = true;
                _cachedStringBuilder.Append("\"");
                _cachedStringBuilder.Append(player.Id);
                _cachedStringBuilder.Append("\"");
                _cachedStringBuilder.Append(",");
            }

            if (addedPlayers) _cachedStringBuilder.Remove(_cachedStringBuilder.Length - 1, 1);

            _cachedStringBuilder.Append("]}");

            return _cachedStringBuilder.ToString();
        }

        class CommandHistory
        {
            readonly Queue<string> _queue;
            readonly int _capacity;

            public CommandHistory(int capacity)
            {
                _capacity = capacity;
                _queue = new Queue<string>(capacity);
            }

            public void Add(string command)
            {
                if (_queue.Count >= _capacity)
                    _queue.Dequeue();

                _queue.Enqueue(command);
            }

            public bool Contains(string command) => _queue.Contains(command);
        }

        void PrintException(string message, Exception ex) => Interface.Oxide.LogException($"[{Title}] {message}", ex);

        #endregion
    }
}