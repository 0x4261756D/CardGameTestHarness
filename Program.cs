using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CardGameUtils;
using CardGameUtils.Structs;
using static CardGameUtils.Functions;

namespace CardGameTestHarness;

public class Program
{
	static string? corePath;
	static bool shouldProfile = false;
	public static void Main(string[] args)
	{
		corePath = args[0];
		bool stopOnError = false;
		if(args.Length > 2)
		{
			stopOnError = args.Contains("--stop_on_error");
			shouldProfile = args.Contains("--profile");
		}
		int count = 0;
		int successful = 0;
		if(Directory.Exists(args[1]))
		{
			List<string> failedFiles = new();
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
			TestReplay(args[1]);
		}
	}

	private static bool TestReplay(string inputPath)
	{
		Log($"Testing {inputPath}");
		Replay replay = JsonSerializer.Deserialize<Replay>(File.ReadAllText(inputPath), NetworkingConstants.jsonIncludeOption)!;
		string arguments = string.Join(' ', replay.cmdlineArgs) + " --seed=" + replay.seed;
		arguments = arguments.Replace(" --replay=true", "");
		using AnonymousPipeServerStream pipeServerStream = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
		arguments = arguments.Replace("--pipe=", "");
		arguments += " --pipe=" + pipeServerStream.GetClientHandleAsString();
		if(corePath == null)
		{
			Log("No core path specified", severity: LogSeverity.Error);
			return false;
		}
		ProcessStartInfo info = new ProcessStartInfo
		{
			Arguments = shouldProfile ? $"collect -- {corePath} {arguments}" : arguments,
			FileName = shouldProfile ? "dotnet-trace" : corePath,
			WorkingDirectory = Path.GetDirectoryName(corePath),
			RedirectStandardOutput = false,
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
		pipeServerStream.ReadExactly(new byte[1], 0, 1);
		using(TcpClient client0 = new("localhost", port), client1 = new("localhost", port))
		{
			using NetworkStream stream0 = client0.GetStream(), stream1 = client1.GetStream();
			index0 = GetPlayerIndex(stream0, id0, port);
			index1 = GetPlayerIndex(stream1, id1, port);
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
						byte[] bytes = action.fullPacketBytes();
						stream0.Write(bytes, 0, bytes.Length);
					}
					else
					{
						if(stream1.DataAvailable)
						{
							Log($"[{i}]: Core sent something but wanted to send", LogSeverity.Error);
							core.Kill();
							return false;
						}
						byte[] bytes = action.fullPacketBytes();
						stream1.Write(bytes, 0, bytes.Length);
					}
				}
				else
				{
					(byte typeByte, byte[]? bytes) = ReceiveRawPacket((action.player == index0) ? stream0 : stream1);
					if(bytes == null)
					{
						Log($"[{i}]: Could not receive a packet in time", LogSeverity.Error);
						core.Kill();
						return false;
					}
					if(!bytes.SequenceEqual(action.packetContentBytes()))
					{
						if(action.packetContentBytes().Length != bytes.Length)
						{
							Log($"[{i}]: Packets have different lengths: {action.packetContentBytes().Length} vs {bytes.Length}", severity: LogSeverity.Error);
							Log(Encoding.UTF8.GetString(bytes));
							Log("----------------------------");
							Log(Encoding.UTF8.GetString(action.packetContentBytes()));
						}
						else
						{
							Log($"[{i}]: Packet difference:", severity: LogSeverity.Error);
							string replayContent = JsonSerializer.Serialize(action.packetContentBytes());
							string newContent = JsonSerializer.Serialize(bytes);
							for(int j = 0; j < replayContent.Length; j++)
							{
								if(replayContent[j] != newContent[j])
								{
									Log($"[{j}]: {replayContent[j]} vs. {newContent[j]})");
								}
							}
						}
						Console.WriteLine("Update packet?");
						if(Console.ReadLine() == "Y")
						{
							replay.actions[i].packetContent = Convert.ToBase64String(bytes);
							File.WriteAllText(inputPath, JsonSerializer.Serialize(replay, NetworkingConstants.jsonIncludeOption));
						}
						else
						{
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
		core.Close();
		return true;
	}

	private static int GetPlayerIndex(NetworkStream stream, string id, int gamePort)
	{
		byte[] idBytes = Encoding.UTF8.GetBytes(id);
		stream.Write(idBytes, 0, idBytes.Length);
		byte[] buffer = new byte[1];
		stream.ReadExactly(buffer, 0, 1);
		return buffer[0];
	}
}
