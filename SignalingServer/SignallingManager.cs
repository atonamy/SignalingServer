/*
 Created by Arseniy Kucherenko
 08.01.2016
 */


using System;
using System.Collections.Concurrent;
using vtortola.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

namespace SignalingServer
{
	public class SignallingManager
	{
		public enum Status
		{
			[StringValue("")]
			NO_STATUS,
			[StringValue("accepted")]
			ACCEPTED,
			[StringValue("received")]
			RECEIVED,
			[StringValue("connected")]
			CONNECTED,
			[StringValue("peer found")]
			PEER_FOUND,
			[StringValue("in queue")]
			IN_QUEUE,
			[StringValue("disoconnected")]
			DISCONNECTED,
			[StringValue("fail to send")]
			FAIL,
			[StringValue("no peers")]
			NO_PEERS,
			[StringValue("no room")]
			NO_ROOM,
			[StringValue("no connection")]
			NO_CONNECTION,
			[StringValue("request is not recognized")]
			NOT_RECOGNIZED,
		}

		public enum Command {
			[StringValue("no command")]
			NO_COMMAND = 12,
			[StringValue("send")]
			SEND,
			[StringValue("online")]
			ONLINE,
			[StringValue("turn")]
			TURN,
			[StringValue("queue")]
			QUEUE,
		}

		private static IReadOnlyDictionary<String, Command> mMapCommands = new Dictionary<String, Command>() {
			{"send", Command.SEND},
			{"online", Command.ONLINE},
			{"turn",  Command.TURN},
			{"queue", Command.QUEUE}
		};
		private static ConcurrentDictionary<Guid, WebSocket> mConnections = new ConcurrentDictionary<Guid, WebSocket> ();
		private static ConcurrentQueue<Guid> mQueue = new ConcurrentQueue<Guid> ();
		private static ConcurrentDictionary<Guid, Guid> mRooms = new ConcurrentDictionary<Guid, Guid> ();
		private Guid mConnectionId;
		private bool mConnected;
		private Action<string, SignallingManager>mSentFeedback;
		private Action<string, SignallingManager>mReceivedFeedback;

		public bool Connected {
			get {
				return mConnected;
			}
		}

		public Guid ConnectionId {
			get {
				return mConnectionId;
			}
		}

		public Action<string, SignallingManager> SentFeedback {
			set {
				mSentFeedback = value;
			}
		}

		public Action<string, SignallingManager> ReceivedFeedback {
			set {
				mReceivedFeedback = value;
			}
		}


		static public string TurnServer {get; set;}
		static public string TurnServerUsername {get; set;}
		static public string TurnServerPassword {get; set;}

		public SignallingManager (WebSocket ws)
		{
			mSentFeedback = null;
			mReceivedFeedback = null;
			mConnectionId = Guid.NewGuid ();
			if (!mConnections.TryAdd (mConnectionId, ws)) {
				mConnected = false;
				if (ws.IsConnected)
					ws.WriteString (GenerateJsonResponse (Status.NO_CONNECTION, "", null));
			} else if (ws.IsConnected) {
				mConnected = true;
				ws.WriteString (GenerateJsonResponse (Status.CONNECTED, "", new Dictionary<string, JToken>() {
					{"ConnectionId", JToken.FromObject(mConnectionId.ToString("B"))}
				}));
				QueueCommand ("");
			} else
				mConnections.TryRemove (mConnectionId, out ws);
		}



		public async Task HandleConnection(CancellationToken cancellation) {
			string message = await mConnections[mConnectionId].ReadStringAsync(cancellation).ConfigureAwait(false);
			if(mReceivedFeedback != null)
				new Task(() => { mReceivedFeedback.Invoke (message, this); }).Start();
			
			Dictionary<string, JToken> param;
			Command command = ParseJsonRequest (message, out param);
			if (command != Command.NO_COMMAND && (!param.ContainsKey ("Id") || param ["Id"].Type != JTokenType.String)) {
				mConnections [mConnectionId].WriteString (GenerateJsonResponse (Status.NOT_RECOGNIZED, "", new Dictionary<string, JToken> () { {
						"Error",
						JToken.FromObject ("Id parameter is not recognised")
					} }));
				return;
			}


			string id = (command != Command.NO_COMMAND) ? (string)param ["Id"] : "";
				
			switch(command) {
			case Command.SEND:
				SendCommand(id, param);
				break;
			case Command.ONLINE:
				OnlineCommand(id, param);
				break;
			case Command.TURN:
				TurnCommand(id, param);
				break;
			case Command.QUEUE:
				ClearRoom (id);
				QueueCommand (id, param); 
				break;
			case Command.NO_COMMAND:
				mConnections [mConnectionId].WriteString (GenerateJsonResponse (Status.NOT_RECOGNIZED, "", 
					(param.ContainsKey("Error")) ? new Dictionary<string, JToken> () { {
						"Error",
						JToken.FromObject (param["Error"])
					} } : null));
				break;
			}
		}

		public void Close() {
			Guid q;
			WebSocket ws;
			if (mConnections.TryRemove (mConnectionId, out ws)) {
				ClearRoom ("");
				if (mQueue.Count == 1 && mQueue.TryPeek (out q) && q == mConnectionId)
					mQueue.TryDequeue (out q);
			}
		}
	
		protected string GenerateJsonResponse(Enum status, string id, Dictionary<string, JToken> param) {
			
			string result = null;
			JObject json = new JObject ();
			json.Add ("Id", JToken.FromObject(id));
			json.Add ("StatusCode", JToken.FromObject(GetStatusCode(status)));
			json.Add ("Status", JToken.FromObject(StringValue.GetStringValue(status)));
			if(param != null)
				foreach(string key in param.Keys)
					json.Add (key, param[key]);
			result = json.ToString (Formatting.None);
			if(mSentFeedback != null)
				new Task(() => { mSentFeedback.Invoke (result, this); }).Start();
			return result;
		}

		protected Command ParseJsonRequest(string request, out Dictionary<string, JToken> param) {
			param = new Dictionary<string, JToken>();
			Command result = Command.NO_COMMAND;
			try {
				JObject json = JObject.Parse (request);
				string command = (string)json["Command"];
				if(mMapCommands.ContainsKey(command)) {
					result = mMapCommands[command];
					foreach(JProperty p in json.Properties())
						if(p.Name != "Command")
							param.Add(p.Name, p.Value);
				}
			}catch(Exception e) {
				param.Add("Error", e.Message);
				return Command.NO_COMMAND;
			}

			return result;
		}

		protected void QueueCommand(string id, Dictionary<string, JToken> param = null) {

			if (param != null && param.Count != 1) {
				mConnections [mConnectionId].WriteString (GenerateJsonResponse (Status.NOT_RECOGNIZED, id, null));
				return;
			}

			mQueue.Enqueue(mConnectionId);
			Guid[] peers = new Guid[] {Guid.Empty, Guid.Empty};
			if (mQueue.Count > 1) {
				mQueue.TryDequeue (out peers [0]);
				mQueue.TryDequeue (out peers [1]);
				if (mConnections.ContainsKey (peers [0]) && mConnections.ContainsKey (peers [1])) {
					if (mRooms.TryAdd (peers [0], peers [1]) && mRooms.TryAdd (peers [1], peers [0])) {
						mConnections [peers[0]].WriteString (GenerateJsonResponse(Status.PEER_FOUND, "", new Dictionary<string, JToken>() { 
							{"IsInitiator", JToken.FromObject(true)},
							{"Peer", JToken.FromObject(peers[1].ToString ("B"))} 
						} ));
						mConnections [peers[1]].WriteString (GenerateJsonResponse(Status.PEER_FOUND, "", new Dictionary<string, JToken>() { 
							{"IsInitiator", JToken.FromObject(false)}, 
							{"Peer", JToken.FromObject(peers[0].ToString ("B"))} 
						} ));
					}
					else 
						mConnections [mConnectionId].WriteString (GenerateJsonResponse(Status.NO_ROOM, id, null));
				} else
					mConnections [mConnectionId].WriteString (GenerateJsonResponse(Status.NO_PEERS, id, null));
			} else
				mConnections [mConnectionId].WriteString (GenerateJsonResponse(Status.IN_QUEUE, id, null));
		}

		protected void SendCommand(string id, Dictionary<string, JToken> param) {

			if (!param.ContainsKey ("To") || !param.ContainsKey ("Message") || param.Count != 3) {
				mConnections [mConnectionId].WriteString (GenerateJsonResponse (Status.NOT_RECOGNIZED, id, null));
				return;
			}

			try {
				string to = (string)param["To"];
				string message = (string)param["Message"]; 

				Guid receiver = Guid.Parse(to);
				if (mConnections.ContainsKey(receiver)){
					mConnections[receiver].WriteString(GenerateJsonResponse(Status.RECEIVED, "", new Dictionary<string, JToken>() { 
						{"Message", JToken.FromObject(message)}, 
						{"From", JToken.FromObject(mConnectionId.ToString("B"))} 
					}));
					mConnections[mConnectionId].WriteString(GenerateJsonResponse(Status.ACCEPTED, id, new Dictionary<string, JToken>() { 
						{"To", JToken.FromObject(receiver.ToString("B"))} 
					}));
				} else 
					mConnections[mConnectionId].WriteString(GenerateJsonResponse(Status.FAIL, id, new Dictionary<string, JToken>() { 
						{"To", JToken.FromObject(receiver.ToString("B"))} 
					}));
			} catch(Exception e) {
				mConnections[mConnectionId].WriteString(GenerateJsonResponse(Status.NOT_RECOGNIZED, id, new Dictionary<string, JToken>() { 
					{"Error", JToken.FromObject(e.Message)} 
				} ));
			}
				
		}

		protected void OnlineCommand(string id, Dictionary<string, JToken> param) {

			if (param.Count != 1) {
				mConnections [mConnectionId].WriteString (GenerateJsonResponse (Status.NOT_RECOGNIZED, id, null));
				return;
			}

			JArray online_connections = new JArray();
			foreach(Guid connection in mConnections.Keys)
				online_connections.Add(JToken.FromObject(connection.ToString("B")));
			
			mConnections[mConnectionId].WriteString(GenerateJsonResponse(Command.ONLINE, id, new Dictionary<string, JToken>() { 
				{"OnlineConnections", online_connections} 
			} ));
		}

		protected void TurnCommand(string id, Dictionary<string, JToken> param) {

			if (param.Count != 1) {
				mConnections [mConnectionId].WriteString (GenerateJsonResponse (Status.NOT_RECOGNIZED, id, null));
				return;
			}

			JArray uris = new JArray ();
			uris.Add (JToken.FromObject (TurnServer + "?transport=udp"));
			uris.Add (JToken.FromObject (TurnServer + "?transport=tcp"));

			mConnections[mConnectionId].WriteString(GenerateJsonResponse(Command.TURN, id, new Dictionary<string, JToken>() { 
				{"Username", JToken.FromObject(TurnServerUsername)},  
				{"Uris", uris},
				{"Password", JToken.FromObject(TurnServerPassword)},
				{"Ttl", JToken.FromObject("86400")}
			} ));
		}

		protected void ClearRoom(string id) {
			Guid[] q = new Guid[2];
			if(mRooms.TryRemove(mConnectionId, out q[0]) && mConnections.ContainsKey(q[0]) && q[0] != mConnectionId)
				mConnections[q[0]].WriteString (GenerateJsonResponse(Status.DISCONNECTED, id, new Dictionary<string, JToken>() {
					{"ConnectionId",  JToken.FromObject(mConnectionId.ToString("B"))}	
				}));
			if(mRooms.TryRemove(q[0], out q[1]) && mConnections.ContainsKey(q[1]) && q[1] != q[0])
				mConnections[q[1]].WriteString (GenerateJsonResponse(Status.DISCONNECTED, id, new Dictionary<string, JToken>() {
					{"ConnectionId",  JToken.FromObject(q[0].ToString("B"))}	
				}));
		}

		protected int GetStatusCode(Enum code) {
			Command command = Command.NO_COMMAND;
			Status status = Status.NO_STATUS;

			if (code.GetType ().Equals (command.GetType ()))
				return (int)(Command)code;
			else if (code.GetType ().Equals (status.GetType ()))
				return (int)(Status)code;

			return -1;
		}
	}
}

