# Anonymous Chat

Desktop messenger with anonymous pairing, GOST-based message protection, and voice calls. The client is a **WPF application** (not a website). The server is a background ASP.NET Core process that clients connect to over WebSocket.

Version **0.0.1**.

## What it does

- Two users press “find a peer” and the server pairs them.
- After pairing they exchange keys and chat with encrypted, signed messages.
- Voice calls can go peer-to-peer (UDP) or be relayed through the server.

## Projects

| Project | Role |
|---|---|
| `ChatServer` | SignalR server (run this) |
| `ChatClient` | WPF desktop client for Windows |
| `WCF_Chat` / `ChatHost` | Previous WCF stack, kept only as reference |

## Requirements

- [.NET 9 SDK](https://dotnet.microsoft.com/download) to run the server
- .NET Framework 4.8 and a Windows machine to run the WPF client
- Visual Studio 2022 or `dotnet` CLI

## How to run

### 1. Start the server

From the repository root:

```bash
dotnet run --project ChatServer
```

The hub listens at `http://localhost:5000/chat`.  
Open `http://localhost:5000/` in a browser only to check that the process is up — the chat UI is the desktop app.

In Visual Studio: set **ChatServer** as the startup project and press F5.

### 2. Start the client

Run **ChatClient** (F5 with that project, or the built `ChatClient.exe`). Start **two** instances to talk to each other.

If the server is not running, the client shows a connection error.

Default URL is in `ChatClient/App.config`:

```xml
<add key="ChatHubUrl" value="http://localhost:5000/chat" />
```

On another machine point this to `http://SERVER_IP:5000/chat` (and open port 5000).

## Hosting

The server is a normal ASP.NET Core app: HTTP + WebSocket, no Windows / `net.tcp` required.

Docker (from the repository root):

```bash
docker build -t chat-server .
docker run -p 8080:8080 chat-server
```

Then set `ChatHubUrl` to `http://HOST:8080/chat`. Put Caddy or nginx with TLS in front for HTTPS.

Suitable hosts: Timeweb Cloud, Selectel, Hetzner, Railway (with the Dockerfile). Shared PHP hosting will not work.

## Protocol (hub `/chat`)

Client invokes: `CreateUser`, `Connect`, `Disconnect`, `RemoveUserSearch`, `SendMessage`, `SendSignedMessage`, `SendHashProtocol`, `SendHashEquals`, `SendCallRequest`, `SendCallAnswer`, `SendCallEnd`, `RelayVoice`, `SendVoiceKeys`.

Server pushes: `GetConnectionAndPublicKey`, `GetConnectionProtocol`, `CompareHMAC`, `LeftChat`, `MessageNotification`, `MessageCallBack`, `MessageCallBackSigned`, `IncomingCall`, `CallAnswered`, `CallEnded`, `ReceiveVoice`, `ReceiveVoiceKeys`.
