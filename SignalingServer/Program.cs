/*
 Created by Arseniy Kucherenko
 08.01.2016
 */

using System;
using System.Threading;
using System.Net;
using vtortola.WebSockets;
using System.Threading.Tasks;
using System.Configuration;
using System.Security.Cryptography.X509Certificates;

namespace SignalingServer
{
	static class TaskExtensions
	{
		public static void Forget(this Task task)
		{
		}
	}

	class MainClass
	{
		


		static void Log(String line)
		{
			Console.WriteLine (line);
		}


		public static void Main (string[] args)
		{
			var certificate_path = ConfigurationManager.AppSettings ["CertificatePath"];
			var certificate_password = ConfigurationManager.AppSettings["CertificatePassword"];
			SignallingManager.TurnServer = ConfigurationManager.AppSettings["TurnServer"];
			SignallingManager.TurnServerUsername = ConfigurationManager.AppSettings["TurnServerUsername"];
			SignallingManager.TurnServerPassword = ConfigurationManager.AppSettings["TurnServerPassword"];
			string server_type = ConfigurationManager.AppSettings ["ServerType"];
			X509Certificate2 certificate = (!string.IsNullOrEmpty(certificate_path) && !string.IsNullOrEmpty(certificate_password)) ? new X509Certificate2(certificate_path, certificate_password) : null;
			IPAddress ip = IPAddress.None;
			CancellationTokenSource cancellation = new CancellationTokenSource();

			if (server_type.ToLower().Equals ("internal"))
				ip = IPAddress.Parse ("127.0.0.1");
			else if (server_type.ToLower().Equals ("external"))
				ip = IPAddress.Any;

			var endpoint = new IPEndPoint(ip, ushort.Parse(ConfigurationManager.AppSettings["ServerPort"]));
			WebSocketListener server = new WebSocketListener(endpoint, new WebSocketListenerOptions(){ SubProtocols = new []{"text"}});
			var rfc6455 = new vtortola.WebSockets.Rfc6455.WebSocketFactoryRfc6455(server);
			server.Standards.RegisterStandard(rfc6455);
			if (certificate != null) {
				Log("SSL is on");
				server.ConnectionExtensions.RegisterExtension (new WebSocketSecureConnectionExtension (certificate));
			}
			server.Start();


			Log("Server started at " + endpoint.ToString());

			var task = Task.Run(() => AcceptWebSocketClientsAsync(server, cancellation.Token));

			Console.ReadKey(true);
			Log("Server stoping");
			cancellation.Cancel();
			task.Wait();
			Console.ReadKey(true);
		}

		static async Task AcceptWebSocketClientsAsync(WebSocketListener server, CancellationToken token)
		{
			
			while (!token.IsCancellationRequested)
			{
				try
				{
					var ws = await server.AcceptWebSocketAsync(token);

					if (ws != null)
						Task.Run(()=>HandleConnectionAsync(ws, token)).Forget();
				}
				catch(Exception aex)
				{
					Log("Error Accepting clients: " + aex.GetBaseException().Message);
				}
			}
			Log("Server Stop accepting clients");
		}

		static async Task HandleConnectionAsync(WebSocket ws, CancellationToken cancellation)
		{
			SignallingManager signalling_manager = null;

			try
			{	
				signalling_manager = new SignallingManager(ws);
				signalling_manager.ReceivedFeedback = IncomingFeedback;
				signalling_manager.SentFeedback = OutgoingFeedback;
				Log("Connected " + signalling_manager.ConnectionId.ToString("B"));
				while (ws.IsConnected && !cancellation.IsCancellationRequested)
				{
					await signalling_manager.HandleConnection(cancellation);
				}
			}
			catch (Exception aex)
			{
				string message = aex.GetBaseException ().Message;
				if (message.Equals ("The connection is closed"))
					Log("Connection closed with " + signalling_manager.ConnectionId.ToString("B"));
				else
					Log("Error Handling connection: " + message);
				try { ws.Close(); }
				catch { }
			}
			finally
			{
				ws.Dispose();
				if (signalling_manager != null)
					signalling_manager.Close ();
			}

		}

		static void IncomingFeedback(string data, SignallingManager manager) {
			Log("Received from client (" + manager.ConnectionId.ToString("B") + "): " + data);
		}

		static void OutgoingFeedback(string data, SignallingManager manager) {
			Log("Sent to client (" + manager.ConnectionId.ToString("B") + "): " + data);
		}
	}
}
