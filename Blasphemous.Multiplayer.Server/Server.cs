using Blasphemous.Multiplayer.Common;
using Blasphemous.Multiplayer.Common.Enums;
using Blasphemous.Multiplayer.Server.Models;
using SimpleTCP;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Blasphemous.Multiplayer.Server;

public class Server
{
    private readonly int _maxPlayers;
    private readonly string _password;

    private SimpleTcpServer server;
    private Dictionary<string, PlayerInfo> connectedPlayers;
    private readonly List<string> _connectionOrder = new List<string>();
    private SceneAuthorityManager _sceneAuthority = new SceneAuthorityManager();
    private readonly object _diagnosticLock = new object();
    private string _diagnosticSessionId;
    private string _diagnosticCollectorIp;
    private Stopwatch _diagnosticSessionClock;
    private long _diagnosticRelaySequence;
    private readonly Queue<byte[]> _diagnosticHistory = new Queue<byte[]>();
    private const int DiagnosticHistoryLimit = 256;

    public Server(int maxPlayers, string password)
    {
        // Change how this works later
        _maxPlayers = maxPlayers;
        _password = password;
    }

    public Dictionary<string, PlayerInfo> getPlayers()
    {
        return connectedPlayers;
    }

    public bool DelayDisabled
    {
        get => server != null && server.DelayDisabled;
        set
        {
            if (server != null)
                server.DelayDisabled = value;
        }
    }

    public bool Start(int port)
    {
        connectedPlayers = new Dictionary<string, PlayerInfo>();
        _connectionOrder.Clear();
        _sceneAuthority = new SceneAuthorityManager();
        _diagnosticSessionId = Guid.NewGuid().ToString("N");
        _diagnosticCollectorIp = null;
        _diagnosticRelaySequence = 0;
        _diagnosticHistory.Clear();
        _diagnosticSessionClock = Stopwatch.StartNew();

        try
        {
            server = new SimpleTcpServer();
            server.ClientConnected += clientConnected;
            server.ClientDisconnected += clientDisconnected;
            server.DataReceived += Receive;
            server.Start(port);
            server.DelayDisabled = true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }

        return true;
    }

    private void Send(string ip, byte[] data, NetworkType dataType)
    {
        if (data != null && data.Length > 0)
        {
            List<byte> list = new List<byte>(BitConverter.GetBytes((ushort)data.Length));
            list.Add((byte)dataType);
            list.AddRange(data);

            try
            {
                server.Send(ip, list.ToArray());
            }
            catch (Exception)
            {
                Logger.Error("Couldn't send data to " + ip + "!");
            }
        }
    }

    // Data should be formatted as length length type data
    private void Receive(object sender, SimpleTCP.DataReceivedEventArgs e)
    {
        int startIdx = 0;
        while (startIdx < e.data.Length - 3)
        {
            ushort length = BitConverter.ToUInt16(e.data, startIdx);
            NetworkType type = (NetworkType)e.data[startIdx + 2];
            byte[] messageData = e.data[(startIdx + 3)..(startIdx + 3 + length)];

            //Logger.Info($"Received {type} packet: {length} bytes");
            processDataReceived(type, messageData);
            startIdx += 3 + length;
        }
        if (startIdx != e.data.Length)
            Logger.Error("Received data was formatted incorrectly");

        // Determines which received function to call based on the type
        void processDataReceived(NetworkType type, byte[] data)
        {
            switch (type)
            {
                case NetworkType.Position:      ReceivePosition(e.ip, data); break;
                case NetworkType.Animation:     ReceiveAnimation(e.ip, data); break;
                case NetworkType.Direction:     ReceiveDirection(e.ip, data); break;
                case NetworkType.EnterScene:    receivePlayerEnterScene(e.ip, data); break;
                case NetworkType.LeaveScene:    receivePlayerLeaveScene(e.ip, data); break;
                case NetworkType.Skin:          receivePlayerSkin(e.ip, data); break;
                case NetworkType.Team:          receivePlayerTeam(e.ip, data); break;
                case NetworkType.Intro:         receivePlayerIntro(e.ip, data); break;
                case NetworkType.Progress:      receivePlayerProgress(e.ip, data); break;
                case NetworkType.Attack:        receivePlayerAttack(e.ip, data); break;
                case NetworkType.Effect:        receivePlayerEffect(e.ip, data); break;
                case NetworkType.Ping:          ReceivePing(e.ip, data); break;
                case NetworkType.WorldReset:   receivePlayerWorldReset(e.ip, data); break;
                case NetworkType.EnemyDeath:   receivePlayerEnemyDeath(e.ip, data); break;
                case NetworkType.EnemyHealth:  receivePlayerEnemyHealth(e.ip, data); break;
                case NetworkType.EnemyHit:     receivePlayerEnemyHit(e.ip, data); break;
                case NetworkType.EnemyPosition: receivePlayerEnemyPosition(e.ip, data); break;
                case NetworkType.PlayerImpact: receivePlayerImpact(e.ip, data); break;
                case NetworkType.EnemyAttack: receivePlayerEnemyAttack(e.ip, data); break;
                case NetworkType.DiagnosticBatch: receiveDiagnosticBatch(e.ip, data); break;
                default:
                    Logger.Error($"Data type '{type}' is not valid"); break;
            }
        }
    }

    private void clientConnected(object sender, ClientConnectionEventArgs e)
    {
        Logger.Info("Client connected at " + e.ip);
    }

    private void clientDisconnected(object sender, ClientConnectionEventArgs e)
    {
        Logger.Info("Client disconnected at " + e.ip);

        // Make sure this client was actually connected, not just rejected from server
        if (!connectedPlayers.ContainsKey(e.ip))
            return;

        // Send that this player has disconnected & remove them.
        sendPlayerConnection(e.ip, false);
        bool collectorDisconnected;
        lock (_diagnosticLock)
            collectorDisconnected = _diagnosticCollectorIp == e.ip;

        List<SceneAuthorityManager.Update> sceneUpdates = _sceneAuthority.Leave(e.ip);
        connectedPlayers.Remove(e.ip);
        _connectionOrder.Remove(e.ip);
        ApplySceneAuthorityUpdates(sceneUpdates);

        if (collectorDisconnected)
            AssignDiagnosticCollector();

        Core.removeUnusedGameData(connectedPlayers);
    }

    private PlayerInfo getCurrentPlayer(string ip)
    {
        if (connectedPlayers.ContainsKey(ip))
            return connectedPlayers[ip];

        Logger.Warn("Data for " + ip + " has not been created yet!");
        return new PlayerInfo(string.Empty, 1);
    }

    private List<byte> addPlayerNameToData(string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);

        List<byte> data = new List<byte>();
        data.Add((byte)nameBytes.Length);
        data.AddRange(nameBytes);
        return data;
    }

    // Position

    private void SendPosition(string playerIp)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]))
            {
                // Send this player's updated position
                Send(ip, GetPositionPacket(current), NetworkType.Position);
            }
        }
    }

    private void ReceivePosition(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        current.xPos = BitConverter.ToSingle(data, 0);
        current.yPos = BitConverter.ToSingle(data, 4);

        SendPosition(playerIp);
    }

    private byte[] GetPositionPacket(PlayerInfo player)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.AddRange(BitConverter.GetBytes(player.xPos));
        bytes.AddRange(BitConverter.GetBytes(player.yPos));
        return bytes.ToArray();
    }

    // Animation

    private void SendAnimation(string playerIp)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]))
            {
                // Send this player's updated animation
                Send(ip, GetAnimationPacket(current), NetworkType.Animation);
            }
        }
    }

    private void ReceiveAnimation(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        current.animation = data[0];

        SendAnimation(playerIp);
    }

    private byte[] GetAnimationPacket(PlayerInfo player)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.Add(player.animation);
        return bytes.ToArray();
    }

    // Direction

    private void SendDirection(string playerIp)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]))
            {
                // Send this player's updated direction
                Send(ip, GetDirectionPacket(current), NetworkType.Direction);
            }
        }
    }

    private void ReceiveDirection(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        current.facingDirection = BitConverter.ToBoolean(data);

        SendDirection(playerIp);
    }

    private byte[] GetDirectionPacket(PlayerInfo player)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.AddRange(BitConverter.GetBytes(player.facingDirection));
        return bytes.ToArray();
    }

    // Enter scene

    private void sendPlayerEnterScene(string playerIp) // Optimize these send functions to combine them together
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip)
            {
                // Send that this player has entered a new scene
                Send(ip, getScenePacket(current), NetworkType.EnterScene);

                if (current.isInSameScene(connectedPlayers[ip]))
                {
                    // If in same scene, also send position data to each one
                    Send(ip, GetPositionPacket(current), NetworkType.Position);
                    Send(ip, GetAnimationPacket(current), NetworkType.Animation);
                    Send(ip, GetDirectionPacket(current), NetworkType.Direction);

                    Send(playerIp, GetPositionPacket(connectedPlayers[ip]), NetworkType.Position);
                    Send(playerIp, GetAnimationPacket(connectedPlayers[ip]), NetworkType.Animation);
                    Send(playerIp, GetDirectionPacket(connectedPlayers[ip]), NetworkType.Direction);
                }
            }
        }
    }

    private void receivePlayerEnterScene(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        string nextScene = Encoding.UTF8.GetString(data);

        // Treat EnterScene as a move if a previous LeaveScene was missed.
        // This keeps the player roster and scene-host membership from diverging.
        if (!string.IsNullOrEmpty(current.sceneName) &&
            !string.Equals(current.sceneName, nextScene, StringComparison.Ordinal))
        {
            sendPlayerLeaveScene(playerIp);
            current.sceneName = string.Empty;
        }

        current.sceneName = nextScene;
        sendPlayerEnterScene(playerIp);
        ApplySceneAuthorityUpdates(_sceneAuthority.Enter(playerIp, current.name, current.sceneName));
    }

    private byte[] getScenePacket(PlayerInfo player)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.AddRange(Encoding.UTF8.GetBytes(player.sceneName));
        return bytes.ToArray();
    }

    // Leave scene

    private void sendPlayerLeaveScene(string playerIp)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        current.xPos = 0;
        current.yPos = 0;
        current.animation = 0;
        current.facingDirection = false;

        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip)
            {
                // Send that this player has left their old scene
                Send(ip, Encoding.UTF8.GetBytes(current.name), NetworkType.LeaveScene);
            }
        }
    }

    private void receivePlayerLeaveScene(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);

        sendPlayerLeaveScene(playerIp);
        List<SceneAuthorityManager.Update> sceneUpdates = _sceneAuthority.Leave(playerIp);
        current.sceneName = "";
        ApplySceneAuthorityUpdates(sceneUpdates);
    }


    // Server-assigned scene authority

    private void SendSceneAuthoritySnapshots(string playerIp)
    {
        foreach (SceneAuthorityManager.Update snapshot in _sceneAuthority.GetSnapshots())
            SendSceneAuthority(playerIp, snapshot);
    }

    private void ApplySceneAuthorityUpdates(List<SceneAuthorityManager.Update> updates)
    {
        if (updates == null)
            return;

        foreach (SceneAuthorityManager.Update update in updates)
        {
            foreach (string recipientIp in update.Recipients)
            {
                if (connectedPlayers.ContainsKey(recipientIp))
                    SendSceneAuthority(recipientIp, update);
            }

            Logger.Info($"Scene authority changed: scene={update.SceneName}, host={(string.IsNullOrEmpty(update.HostName) ? "<none>" : update.HostName)}, epoch={update.Epoch}");
        }
    }

    private void SendSceneAuthority(string playerIp, SceneAuthorityManager.Update update)
    {
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(update.SceneName);
            writer.Write(update.HostName);
            writer.Write(update.Epoch);
            Send(playerIp, stream.ToArray(), NetworkType.SceneAuthority);
        }
    }

    // Skin

    private void sendPlayerSkin(string playerIp)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip)
            {
                // Send this player's updated skin
                Send(ip, getSkinPacket(current), NetworkType.Skin);
            }
        }
    }

    private void receivePlayerSkin(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        current.skin = data;

        sendPlayerSkin(playerIp);
    }

    private byte[] getSkinPacket(PlayerInfo player)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.AddRange(player.skin);
        return bytes.ToArray();
    }

    // Team

    private void sendPlayerTeam(string playerIp)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip)
            {
                Send(ip, getTeamPacket(current), NetworkType.Team);
            }
        }

        // Send all of the server data to this player for them to merge
        Logger.Info("Sending all server data to " + playerIp);
        for (byte i = 0; i < TeamInfo.NUMBER_OF_PROGRESS_TYPES; i++)
        {
            Dictionary<string, byte> progressSet = Core.getTeamData(current.team).GetTeamProgressSet(i);
            foreach (string id in progressSet.Keys)
            {
                byte value = progressSet[id];
                if (i == 6 && id == "FLASK")
                {
                    Logger.Info("Send all server flask: " + value);
                    value -= Core.getTeamData(current.team).GetTeamProgressValue(6, "FLASKHEALTH");
                    Logger.Info("Send all send flask: " + value);
                }
                Send(playerIp, getProgressPacket("*", i, value, id), NetworkType.Progress);
            }
        }
    }

    private void receivePlayerTeam(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        current.team = data[0];

        sendPlayerTeam(playerIp);
        Core.removeUnusedGameData(connectedPlayers);
    }

    private byte[] getTeamPacket(PlayerInfo player)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.Add(player.team);
        return bytes.ToArray();
    }

    // Connection

    private void sendPlayerConnection(string playerIp, bool connected)
    {
        // Send message to all other connected ips
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip)
            {
                Send(ip, getConnectionPacket(current, connected), NetworkType.Connection);
            }
        }
    }

    private byte[] getConnectionPacket(PlayerInfo player, bool connected)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.AddRange(BitConverter.GetBytes(connected));
        return bytes.ToArray();
    }

    // Intro

    private void sendPlayerIntro(string playerIp, RefusalType response)
    {
        Send(playerIp, getIntroPacket((byte)response), NetworkType.Intro);

        // Only send rest of data if successful connection
        if (response > 0)
            return;

        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip)
            {
                // Send all other connected players and their important data
                Send(playerIp, getConnectionPacket(connectedPlayers[ip], true), NetworkType.Connection);
                Send(playerIp, getSkinPacket(connectedPlayers[ip]), NetworkType.Skin);
                Send(playerIp, getTeamPacket(connectedPlayers[ip]), NetworkType.Team);
                if (connectedPlayers[ip].sceneName != "")
                {
                    Send(playerIp, getScenePacket(connectedPlayers[ip]), NetworkType.EnterScene);
                    if (current.isInSameScene(connectedPlayers[ip]))
                    {
                        Send(playerIp, GetPositionPacket(connectedPlayers[ip]), NetworkType.Position);
                        Send(playerIp, GetAnimationPacket(connectedPlayers[ip]), NetworkType.Animation);
                        Send(playerIp, GetDirectionPacket(connectedPlayers[ip]), NetworkType.Direction);
                    }
                }
            }
        }

        SendSceneAuthoritySnapshots(playerIp);
    }

    private void receivePlayerIntro(string playerIp, byte[] data)
    {
        int idx = 0;

        // Ensure the server doesn't have max number of players
        if (connectedPlayers.Count >= _maxPlayers)
        {
            Logger.Warn("Player connection rejected: Player limit reached");
            sendPlayerIntro(playerIp, RefusalType.PlayerLimit);
            return;
        }

        // Ensure there are no duplicate ips
        if (connectedPlayers.ContainsKey(playerIp))
        {
            Logger.Warn("Player connection rejected: Duplicate ip address");
            sendPlayerIntro(playerIp, RefusalType.DuplicateIp);
            return;
        }

        byte protocol = data[idx++];

        // Ensure the protocol version matches
        if (protocol != Protocol.VERSION)
        {
            Logger.Warn("Player connection rejected: Protocol version doesn't match");
            sendPlayerIntro(playerIp, RefusalType.Protocol);
            return;
        }

        byte roomLength = data[idx];
        string room = Encoding.UTF8.GetString(data, idx + 1, roomLength);
        idx += roomLength + 1;

        byte nameLength = data[idx];
        string name = Encoding.UTF8.GetString(data, idx + 1, nameLength);
        idx += nameLength + 1;

        // Ensure there are no duplicate names
        foreach (PlayerInfo player in connectedPlayers.Values)
        {
            if (player.name == name)
            {
                Logger.Warn("Player connection rejected: Duplicate name");
                sendPlayerIntro(playerIp, RefusalType.DuplicateName);
                return;
            }
        }

        byte passwordLength = data[idx];
        string password = passwordLength == 0 ? null : Encoding.UTF8.GetString(data, idx + 1, passwordLength);
        idx += passwordLength + 1;

        // Ensure the password is correct
        if (!string.IsNullOrEmpty(_password) && (password == null || password != _password))
        {
            Logger.Warn("Player connection rejected: Incorrect password");
            sendPlayerIntro(playerIp, RefusalType.Password);
            return;
        }

        byte team = data[idx];

        // Add new connected player
        Logger.Info("Player connection accepted");
        PlayerInfo newPlayer = new PlayerInfo(name, team);
        connectedPlayers.Add(playerIp, newPlayer);
        _connectionOrder.Add(playerIp);
        bool collectorAssigned = false;
        lock (_diagnosticLock)
        {
            if (string.IsNullOrEmpty(_diagnosticCollectorIp))
            {
                _diagnosticCollectorIp = playerIp;
                collectorAssigned = true;
            }
        }

        sendPlayerConnection(playerIp, true);
        sendPlayerIntro(playerIp, RefusalType.Accepted);
        BroadcastDiagnosticCollectorAssignment();
        if (collectorAssigned)
            ReplayDiagnosticHistory();
    }

    private byte[] getIntroPacket(byte response)
    {
        return new byte[] { response };
    }

    // Progress

    private void sendPlayerProgress(string playerIp, byte type, byte value, string id)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.team == getCurrentPlayer(ip).team)
            {
                Send(ip, getProgressPacket(current.name, type, value, id), NetworkType.Progress);
            }
        }
    }

    private void receivePlayerProgress(string playerIp, byte[] data)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        byte progressType = data[0];
        byte progressValue = data[1];
        string progressId = Encoding.UTF8.GetString(data, 2, data.Length - 2);

        // Add the progress to the server data, and if it's new send it to the rest of the players
        if (!Core.getTeamData(current.team).AddTeamProgress(progressId, progressType, progressValue))
        {
            Logger.ProgressBad($"Received duplicated or inferior progress from {current.name}: {progressId}, Type {progressType}, Value {progressValue}");
            return;
        }

        if (progressType >= 0 && progressType <= 5)
        {
            // Item
            Logger.ProgressGood($"{(progressValue == 0 ? "Received new" : "Lost an")} item from {current.name}: {progressId}");
        }
        else if (progressType == 6)
        {
            // Stat
            Logger.ProgressGood($"Received new stat upgrade from {current.name}: {progressId} level {progressValue + 1}");
        }
        else if (progressType == 7)
        {
            // Skill
            Logger.ProgressGood($"Received new skill from {current.name}: {progressId}");
        }
        else if (progressType == 8)
        {
            // Map cell
            Logger.ProgressGood($"Received new map cell from {current.name}: {progressId}");
        }
        else if (progressType == 9)
        {
            // Flag
            Logger.ProgressGood($"Received new flag from {current.name}: {progressId}");
        }
        else if (progressType == 10)
        {
            // Pers. object
            Logger.ProgressGood($"Received new pers. object from {current.name}: {progressId}");
        }
        else if (progressType == 11)
        {
            // Teleport
            Logger.ProgressGood($"Received new teleport location from {current.name}: {progressId}");
        }
        else if (progressType == 12)
        {
            // Church donation
            Logger.ProgressGood($"Received new tear donation from {current.name}: {progressValue}");
        }
        else if (progressType == 13)
        {
            // Miriam status
            Logger.ProgressGood($"Received new miriam status from {current.name}: {progressId}");
        }

        // If this is a stat upgrade, might have to do something extra with flask/flaskhealth
        if (progressType == 6)
        {
            if (progressId == "FLASK")
            {
                Logger.Info("Received flask level: " + progressValue);
                byte flaskHealthUpgrades = Core.getTeamData(current.team).GetTeamProgressValue(6, "FLASKHEALTH");
                progressValue -= flaskHealthUpgrades;
                Logger.Info("Flask level sent out: " + progressValue);
            }
            else if (progressId == "FLASKHEALTH")
            {
                byte flaskUpgrades = Core.getTeamData(current.team).GetTeamProgressValue(6, "FLASK");
                Logger.Info("Flask level stored: " + flaskUpgrades);
                Logger.Info("Flask level sent: " + (byte)(flaskUpgrades - progressValue));
                sendPlayerProgress(playerIp, 6, (byte)(flaskUpgrades - progressValue), "FLASK");
            }
        }

        sendPlayerProgress(playerIp, progressType, progressValue, progressId);
    }

    private byte[] getProgressPacket(string nameOrStar, byte type, byte value, string id)
    {
        List<byte> bytes = addPlayerNameToData(nameOrStar);
        bytes.Add(type);
        bytes.Add(value);
        bytes.AddRange(Encoding.UTF8.GetBytes(id));
        return bytes.ToArray();
    }

    // Attack

    private void sendPlayerAttack(string playerIp, byte[] attackData)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]))
            {
                // Send this player's attack
                Send(ip, getAttackPacket(current, attackData), NetworkType.Attack);
            }
        }
    }

    private void receivePlayerAttack(string playerIp, byte[] data)
    {
        sendPlayerAttack(playerIp, data);
    }

    private byte[] getAttackPacket(PlayerInfo player, byte[] attackData)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.AddRange(attackData);
        return bytes.ToArray();
    }

    // Shared Prie Dieu enemy respawn

    private void receivePlayerWorldReset(string playerIp, byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            Logger.Warn("Received invalid world reset packet");
            return;
        }

        PlayerInfo current = getCurrentPlayer(playerIp);
        string sceneName = Encoding.UTF8.GetString(data);

        // Only accept a reset for the sender's current scene.
        if (current == null || string.IsNullOrEmpty(sceneName) || current.sceneName != sceneName)
        {
            Logger.Warn("Rejected world reset from invalid scene");
            return;
        }

        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]) && connectedPlayers[ip].sceneName == sceneName)
                Send(ip, data, NetworkType.WorldReset);
        }
    }

    // Effect

    private void sendPlayerEffect(string playerIp, byte effect)
    {
        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]))
            {
                // Send this player's attack
                Send(ip, getEffectPacket(current, effect), NetworkType.Effect);
            }
        }
    }

    private void receivePlayerEffect(string playerIp, byte[] data)
    {
        sendPlayerEffect(playerIp, data[0]);
    }

    private byte[] getEffectPacket(PlayerInfo player, byte effect)
    {
        List<byte> bytes = addPlayerNameToData(player.name);
        bytes.Add(effect);
        return bytes.ToArray();
    }

    // Player impact effects

    private void receivePlayerImpact(string playerIp, byte[] data)
    {
        if (data.Length != 15)
            return;

        PlayerInfo current = getCurrentPlayer(playerIp);
        foreach (string ip in connectedPlayers.Keys)
        {
            if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]))
            {
                List<byte> bytes = addPlayerNameToData(current.name);
                bytes.AddRange(data);
                Send(ip, bytes.ToArray(), NetworkType.PlayerImpact);
            }
        }
    }

    // Ping

    private void SendPing(string playerIp, float time)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(time));

        foreach (var player in getPlayers())
        {
            if (player.Key == playerIp)
                continue;

            string name = player.Value.name;
            ushort ping = player.Value.ping;

            bytes.Add((byte)name.Length);
            bytes.AddRange(Encoding.UTF8.GetBytes(name));
            bytes.AddRange(BitConverter.GetBytes(ping));
        }

        Send(playerIp, bytes.ToArray(), NetworkType.Ping);
    }

    private void ReceivePing(string playerIp, byte[] data)
    {
        float time = BitConverter.ToSingle(data, 0);
        ushort ping = BitConverter.ToUInt16(data, 4);

        PlayerInfo current = getCurrentPlayer(playerIp);
        current.ping = ping;

        SendPing(playerIp, time);
    }

    // Shared enemy death

    private void receivePlayerEnemyDeath(string playerIp, byte[] data)
    {
        if (data == null || data.Length == 0)
            return;

        try
        {
            string sceneName;
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                sceneName = reader.ReadString();
                string spawnPointId = reader.ReadString();
                int generation = reader.ReadInt32();
                if (stream.Position != stream.Length || string.IsNullOrEmpty(spawnPointId) || generation < 0)
                    return;
            }

            PlayerInfo current = getCurrentPlayer(playerIp);
            if (current == null || string.IsNullOrEmpty(sceneName) || current.sceneName != sceneName)
                return;

            // Enemy deaths are session-wide world state, not scene-local player state.
            // Peers in other scenes retain the event and reconcile it when they enter.
            foreach (string ip in connectedPlayers.Keys)
            {
                if (playerIp != ip)
                    Send(ip, data, NetworkType.EnemyDeath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Ignored malformed enemy death packet: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Shared enemy health delta

    private void receivePlayerEnemyHealth(string playerIp, byte[] data)
    {
        if (data == null || data.Length == 0)
            return;

        try
        {
            string sceneName;
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                sceneName = reader.ReadString();
                string spawnPointId = reader.ReadString();
                int generation = reader.ReadInt32();
                float damage = reader.ReadSingle();
                if (stream.Position != stream.Length || string.IsNullOrEmpty(spawnPointId) || generation < 0 ||
                    damage <= 0f || float.IsNaN(damage) || float.IsInfinity(damage))
                    return;
            }

            PlayerInfo current = getCurrentPlayer(playerIp);
            if (current == null || string.IsNullOrEmpty(sceneName) || current.sceneName != sceneName)
                return;

            foreach (string ip in connectedPlayers.Keys)
            {
                if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]) && connectedPlayers[ip].sceneName == sceneName)
                    Send(ip, data, NetworkType.EnemyHealth);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Ignored malformed enemy health packet: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void receivePlayerEnemyHit(string playerIp, byte[] data)
    {
        try
        {
            string sceneName;
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                sceneName = reader.ReadString(); string spawnPointId = reader.ReadString();
                int generation = reader.ReadInt32(); byte orientation = reader.ReadByte();
                if (stream.Position != stream.Length || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || orientation > 1) return;
            }
            PlayerInfo current = getCurrentPlayer(playerIp);
            if (current == null || current.sceneName != sceneName) return;
            foreach (string ip in connectedPlayers.Keys)
                if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]) && connectedPlayers[ip].sceneName == sceneName)
                    Send(ip, data, NetworkType.EnemyHit);
        }
        catch (Exception ex) { Logger.Warn($"Ignored malformed enemy hit packet: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void receivePlayerEnemyPosition(string playerIp, byte[] data)
    {
        try
        {
            string sceneName;
            long epoch;
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                sceneName = reader.ReadString(); string spawnPointId = reader.ReadString();
                int generation = reader.ReadInt32(); epoch = reader.ReadInt64();
                float x = reader.ReadSingle(); float y = reader.ReadSingle(); float z = reader.ReadSingle();
                byte orientation = reader.ReadByte(); byte state = reader.ReadByte(); bool walking = reader.ReadBoolean();
                float currentHealth = reader.ReadSingle();
                if (stream.Position != stream.Length || string.IsNullOrEmpty(sceneName) || string.IsNullOrEmpty(spawnPointId) || generation < 0 || epoch < 0 ||
                    orientation > 1 || state > 4 || currentHealth <= 0f || float.IsNaN(currentHealth) || float.IsInfinity(currentHealth) ||
                    float.IsNaN(x) || float.IsInfinity(x) || float.IsNaN(y) || float.IsInfinity(y) || float.IsNaN(z) || float.IsInfinity(z)) return;
            }
            PlayerInfo current = getCurrentPlayer(playerIp);
            if (current == null || current.sceneName != sceneName || !_sceneAuthority.IsHost(playerIp, sceneName, epoch))
            {
                Logger.Warn($"Rejected enemy position from non-authority or stale epoch: player={current?.name}, scene={sceneName}, epoch={epoch}");
                return;
            }
            foreach (string ip in connectedPlayers.Keys)
                if (playerIp != ip && current.isInSameScene(connectedPlayers[ip]) && connectedPlayers[ip].sceneName == sceneName)
                    Send(ip, data, NetworkType.EnemyPosition);
        }
        catch (Exception ex) { Logger.Warn($"Ignored malformed enemy position packet: {ex.GetType().Name}: {ex.Message}"); }
    }


    // Shared diagnostics are relayed independently of scene and enemy authority.
    // Every batch is timestamped by the server's monotonic session clock before
    // it reaches the currently assigned collector.
    private void BroadcastDiagnosticCollectorAssignment()
    {
        string collectorIp;
        string collectorName;
        string sessionId;
        long serverElapsedMilliseconds;

        lock (_diagnosticLock)
        {
            collectorIp = _diagnosticCollectorIp;
            sessionId = _diagnosticSessionId;
            serverElapsedMilliseconds = _diagnosticSessionClock == null ? 0 : _diagnosticSessionClock.ElapsedMilliseconds;
        }

        collectorName = !string.IsNullOrEmpty(collectorIp) && connectedPlayers.ContainsKey(collectorIp)
            ? connectedPlayers[collectorIp].name
            : string.Empty;

        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(sessionId ?? string.Empty);
            writer.Write(collectorName);
            writer.Write(serverElapsedMilliseconds);
            byte[] packet = stream.ToArray();

            foreach (string ip in connectedPlayers.Keys)
                Send(ip, packet, NetworkType.DiagnosticControl);
        }

        Logger.Info($"Shared diagnostic collector assigned: {collectorName}");
    }

    private void AssignDiagnosticCollector()
    {
        string nextCollector = null;
        foreach (string ip in _connectionOrder)
        {
            if (connectedPlayers.ContainsKey(ip))
            {
                nextCollector = ip;
                break;
            }
        }

        lock (_diagnosticLock)
            _diagnosticCollectorIp = nextCollector;

        BroadcastDiagnosticCollectorAssignment();
        ReplayDiagnosticHistory();
    }

    private void ReplayDiagnosticHistory()
    {
        string collectorIp;
        List<byte[]> history;
        lock (_diagnosticLock)
        {
            collectorIp = _diagnosticCollectorIp;
            history = new List<byte[]>(_diagnosticHistory);
        }

        if (string.IsNullOrEmpty(collectorIp) || !connectedPlayers.ContainsKey(collectorIp))
            return;

        foreach (byte[] packet in history)
            Send(collectorIp, packet, NetworkType.DiagnosticBatch);
    }

    private void receiveDiagnosticBatch(string playerIp, byte[] data)
    {
        if (data == null || data.Length == 0 || data.Length > 8192 || !connectedPlayers.ContainsKey(playerIp))
            return;

        string collectorIp;
        string sessionId;
        long serverElapsedMilliseconds;
        long relaySequence;

        lock (_diagnosticLock)
        {
            collectorIp = _diagnosticCollectorIp;
            sessionId = _diagnosticSessionId;
            serverElapsedMilliseconds = _diagnosticSessionClock == null ? 0 : _diagnosticSessionClock.ElapsedMilliseconds;
            relaySequence = ++_diagnosticRelaySequence;
        }

        if (string.IsNullOrEmpty(collectorIp) || !connectedPlayers.ContainsKey(collectorIp))
            return;

        PlayerInfo source = connectedPlayers[playerIp];
        byte[] relayPacket;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8))
        {
            writer.Write(sessionId ?? string.Empty);
            writer.Write(serverElapsedMilliseconds);
            writer.Write(relaySequence);
            writer.Write(source.name);
            writer.Write(data.Length);
            writer.Write(data);
            relayPacket = stream.ToArray();
        }

        lock (_diagnosticLock)
        {
            _diagnosticHistory.Enqueue(relayPacket);
            while (_diagnosticHistory.Count > DiagnosticHistoryLimit)
                _diagnosticHistory.Dequeue();
        }

        Send(collectorIp, relayPacket, NetworkType.DiagnosticBatch);
    }

    // Relay authoritative WheelCarrier attacks to every peer in the same scene.
    private void receivePlayerEnemyAttack(string playerIp, byte[] data)
    {
        try
        {
            string sceneName;
            long epoch;
            using (MemoryStream stream = new MemoryStream(data))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8))
            {
                sceneName = reader.ReadString();
                string spawnPointId = reader.ReadString();
                int generation = reader.ReadInt32();
                epoch = reader.ReadInt64();
                byte orientation = reader.ReadByte();
                if (stream.Position != stream.Length || string.IsNullOrEmpty(sceneName) ||
                    string.IsNullOrEmpty(spawnPointId) || generation < 0 || epoch < 0 || orientation > 1) return;
            }

            PlayerInfo sender = getCurrentPlayer(playerIp);
            if (sender == null || sender.sceneName != sceneName || !_sceneAuthority.IsHost(playerIp, sceneName, epoch))
            {
                Logger.Warn($"Rejected enemy attack from non-authority or stale epoch: player={sender?.name}, scene={sceneName}, epoch={epoch}");
                return;
            }

            foreach (KeyValuePair<string, PlayerInfo> pair in connectedPlayers)
            {
                PlayerInfo peer = pair.Value;
                if (pair.Key != playerIp && peer != null && peer.sceneName == sceneName)
                {
                    List<byte> packet = addPlayerNameToData(sender.name);
                    packet.AddRange(data);
                    Send(pair.Key, packet.ToArray(), NetworkType.EnemyAttack);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Ignored malformed enemy attack packet: {ex.GetType().Name}: {ex.Message}");
        }
    }

}
