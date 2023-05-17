using System.Text.Json;
using CardGameUtils;
using static CardGameUtils.Functions;
using CardGameUtils.Structs;
using System.Text;
using System.Diagnostics;
using System.Net.Sockets;

namespace CardGameTestHarness;

public class Program
{
	static string? corePath;
	public static void Main(string[] args)
	{
		corePath = args[0];
		bool stopOnError = false;
		if(args.Length > 2)
		{
			if(args.Contains("--stop_on_error"))
			{
				stopOnError = true;
			}
		}
		int count = 0;
		int successful = 0;
		if(Directory.Exists(args[1]))
		{
			List<string> failedFiles = new List<string>();
			foreach(string file in Directory.EnumerateFiles(args[1]))
			{
				if(TestReplay(file))
				{
					successful++;
				}
				else if(stopOnError)
				{
					Log("Successful runs: " + successful);
					return;
				}
				else
				{
					failedFiles.Add(file);
				}
				count++;
			}
			Log("======STATISTICS=======");
			foreach(string file in failedFiles)
			{
				Log(file);
			}
			Log($"Passed: {successful}/{count}");
		}
		else
		{
			TestReplay(args[0]);
		}
	}

	private static bool TestReplay(string inputPath)
	{
		Log($"Testing {inputPath}");
		Replay replay = JsonSerializer.Deserialize<Replay>(File.ReadAllText(inputPath), NetworkingConstants.jsonIncludeOption)!;
		string arguments = String.Join(' ', replay.cmdlineArgs) + " --seed=" + replay.seed;
		arguments = arguments.Replace(" --replay=true", "");
		if(corePath == null)
		{
			Log("No core path specified", severity: LogSeverity.Error);
			return false;
		}
		ProcessStartInfo info = new ProcessStartInfo
		{
			Arguments = arguments,
			FileName = corePath,
			WorkingDirectory = Path.GetDirectoryName(corePath),
			RedirectStandardOutput = true,
		};
		string playerString = replay.cmdlineArgs.First(x => x.StartsWith("--players="));
		playerString = playerString.Substring(playerString.IndexOf('=') + 1);
		string[] playerStringParts = Encoding.UTF8.GetString(Convert.FromBase64String(playerString)).Split('µ');
		string id0 = playerStringParts[2];
		string id1 = playerStringParts[5];
		Process core = Process.Start(info)!;
		core.Exited += (_, _) => { Console.WriteLine("exited"); };
		int port = Convert.ToInt32(replay.cmdlineArgs.First(x => x.StartsWith("--port=")).Split('=')[1]);
		int index0 = 0;
		int index1 = 0;
		using(TcpClient client0 = CheckForReady(id0, port, out index0), client1 = CheckForReady(id1, port, out index1))
		{
			using(NetworkStream stream0 = client0.GetStream(), stream1 = client1.GetStream())
			{
				for(int i = 0; i < replay.actions.Count; i++)
				{
					Replay.GameAction action = replay.actions[i];
					if(action.clientToServer)
					{
						if(action.player == index0)
						{
							if(stream0.DataAvailable)
							{
								Log($"[{i}]: Core sent something but wanted to send", LogSeverity.Error);
								core.Kill();
								return false;
							}
							action.packet.AddRange(NetworkingStructs.Packet.ENDING);
							stream0.Write(action.packet.ToArray(), 0, action.packet.Count);
						}
						else
						{
							if(stream1.DataAvailable)
							{
								Log($"[{i}]: Core sent something but wanted to send", LogSeverity.Error);
								core.Kill();
								return false;
							}
							action.packet.AddRange(NetworkingStructs.Packet.ENDING);
							stream1.Write(action.packet.ToArray(), 0, action.packet.Count);
						}
					}
					else
					{
						List<byte>? packet = ReceiveRawPacket((action.player == index0) ? stream0 : stream1, timeout: 2000);
						if(packet == null)
						{
							Log($"[{i}]: Could not receive a packet in time", LogSeverity.Error);
							core.Kill();
							return false;
						}
						if(!packet.SequenceEqual(action.packet))
						{
							if(action.packet.Count != packet.Count)
							{
								Log($"[{i}]: Packets have different lengths: {action.packet.Count} vs {packet.Count}", severity: LogSeverity.Error);
							}
							else
							{
								Log($"[{i}]: Packet difference:", severity: LogSeverity.Error);
								for(int j = 0; j < packet.Count; j++)
								{
									if(packet[j] != action.packet[j])
									{
										Log($"[{j}]: {packet[j]} vs. {action.packet[j]} ({(char)packet[j]} vs. {(char)action.packet[j]})");
									}
								}
							}
							core.Kill();
							return false;
						}
					}
				}
			}
		}
		Console.BackgroundColor = ConsoleColor.Green;
		Console.Write($"===Passed===");
		Console.ResetColor();
		Console.WriteLine();
		core.Kill();
		return true;
	}

	private static TcpClient CheckForReady(string id, int gamePort, out int playerIndex)
	{
		// AAAAAAAAAAAAAAAHHHHHHHHHH UGLY CODE....
		// TODO: Rework Room to work with a listener instead
		while(true)
		{
			TcpClient c = new TcpClient();
			try
			{
				c.Connect("localhost", gamePort);
				if(c.Connected)
				{
					byte[] idBytes = Encoding.UTF8.GetBytes(id);
					c.GetStream().Write(idBytes, 0, idBytes.Length);
					do
					{
						playerIndex = c.GetStream().ReadByte();
					}
					while(playerIndex == -1);
					return c;
				}
				else
				{
					c.Close();
				}
			}
			catch(SocketException)
			{
				Thread.Sleep(100);
			}
		}
	}

}
