using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FFMpegCore;
using FFMpegCore.Enums;
using Polly;

namespace JPB.AudioSubtitleExpander;

public class MergeSubStreams : WorkerBase
{
	public override async Task Run()
	{
		Console.WriteLine("Source Directory (from which to load the substream):");
		var sourceDir = GetInput("ASE_SourceDirectory");
		if (!Directory.Exists(sourceDir))
		{
			Console.WriteLine("Directory does not exist.");
			return;
		}

		Console.WriteLine("Target Directory (from which to load the video streams):");
		var targetDir = GetInput("ASE_TargetDirectory");
		if (!Directory.Exists(targetDir))
		{
			Console.WriteLine("Directory does not exist.");
			return;
		}

		Console.WriteLine("Output Directory:");
		var outputDir = GetInput("ASE_OutputDirectory") ?? "./";
		if (!Directory.Exists(outputDir))
		{
			Console.WriteLine("Output Directory does not exist.");
			return;
		}

		string[] extensions = { "mkv", "m4v", "mp4" };

		IEnumerable<string> GetFiles(string directory, SearchOption option)
		{
			return extensions.SelectMany(e => Directory.GetFiles(directory, $"*.{e}", option));
		}

		string NormalizeFileName(string file)
		{
			return Path.GetFileNameWithoutExtension(file)
				.Split('-')
				.First()
				.Trim();
		}

		var sourceFiles = GetFiles(sourceDir, SearchOption.AllDirectories)
			.Where(e => extensions.Contains(Path.GetExtension(e).Trim('.').ToLower()));
		var targetFiles = GetFiles(targetDir, SearchOption.TopDirectoryOnly)
			.Where(e => extensions.Contains(Path.GetExtension(e).Trim('.').ToLower()))
			.ToArray();

		var matchesList = new List<(string target, string source)>();
		foreach (var targetPath in targetFiles)
		{
			var targetName = NormalizeFileName(targetPath);
			var sourcePath = sourceFiles
				.FirstOrDefault(e => NormalizeFileName(e).Equals(targetName, StringComparison.OrdinalIgnoreCase));
			if (sourcePath != null)
			{
				matchesList.Add((targetPath, sourcePath));
			}
		}

		var matches = matchesList.ToArray();

		//var matches = targetFiles.Join(sourceFiles,
		//	NormalizeFileName,
		//	NormalizeFileName,
		//	(e, f) => (source: f, target: e))
		//	.ToArray();

		Console.WriteLine($"Matched {matches.Length} titles.");

		for (var index = 0; index < matches.Length; index++)
		{
			var match = matches[index];
			Console.WriteLine($"Process match: {index}/{matches.Length} - {Path.GetFileName(match.target)}");
			var sourceMediaInfo = await Policy.Handle<Exception>()
				.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(5 + i))
				.ExecuteAsync(async () => await FFProbe.AnalyseAsync(match.source));

			var targetMediaInfo = await Policy.Handle<Exception>()
				.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(5 + i))
				.ExecuteAsync(async () => await FFProbe.AnalyseAsync(match.target));

			var targetVideoStreams = targetMediaInfo.VideoStreams;

			var targetFile = Path.Combine(outputDir,
				Path.GetFileNameWithoutExtension(match.target) + Path.GetExtension(match.target));

			Console.WriteLine("Will create new file:");
			var frames = targetMediaInfo.Duration.TotalSeconds * targetMediaInfo.PrimaryVideoStream.AvgFrameRate;
			Console.WriteLine("Video: ");
			foreach (var stream in targetVideoStreams)
			{
				Console.WriteLine($"\t{stream.Index} -> {ExtractSubStreams.RenderMediaStreamText(stream, null)} {stream.AvgFrameRate}fps");
			}
			Console.WriteLine("audio: ");
			foreach (var stream in sourceMediaInfo.AudioStreams)
			{
				Console.WriteLine($"\t{stream.Index} -> {ExtractSubStreams.RenderMediaStreamText(stream, null)} {stream.Language}");
			}
			Console.WriteLine("SubTitles: ");
			foreach (var stream in sourceMediaInfo.SubtitleStreams)
			{
				Console.WriteLine($"\t{stream.Index} -> {ExtractSubStreams.RenderMediaStreamText(stream, null)} {stream.Language}");
			}


			var arguments = $"-i \"{match.target}\" -i \"{match.source}\" -map 0:v:0 -map 1:a -map 1:s -c copy -max_interleave_delta 0 \"{targetFile}\"";
			//var arguments = $"-i \"{match.target}\" -i \"{match.source}\" -map 0:v:0 -map 1:a -map 1:s -c copy -fps_mode:s 0 \"{targetFile}\"";

			//var ffMpegArgumentProcessor = FFMpegArguments
			//	.FromFileInput(match.target, false, args => { })
			//	.AddFileInput(match.source, false, args => { })
			//	.OutputToFile(targetFile, false,
			//		args =>
			//		{
			//			args.WithCustomArgument("-map 0:v:0 -map 1:a -map 1:s -c copy -max_interleave_delta 0");
			//		});
			//var arguments = ffMpegArgumentProcessor.Arguments;

			Console.WriteLine($"Run ffmpeg: {arguments}");

			var process = Process.Start(new ProcessStartInfo(Path.Combine(FfmpegPath, "ffmpeg.exe"))
			{
				Arguments = arguments,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			});
			process.BeginErrorReadLine();
			process.BeginOutputReadLine();
			process.OutputDataReceived += Process_OutputDataReceived;
			process.ErrorDataReceived += (o, args) =>
			{
				if (args.Data is null)
				{
					return;
				}

				if (args.Data.StartsWith("frame="))
				{
					//frame=    1 fps=0.0 q=-1.0 size=       6kB time=00:00:00.00 bitrate=N/A speed=   0x    

					var strings = _progressRegex.Matches(args.Data).First().Groups.Values.Skip(1).Select(e => e.Value.Trim()).ToArray();
					var status = new FFmpegStatus(strings);
					Console.CursorTop--;
					var percentageDone = Math.Round(100 / frames * status.Frames, 2);
					//TimeSpan timeEstimate = TimeSpan.Zero;
					//if (status.Elapsed.TotalSeconds > 0 && percentageDone > 0)
					//{
					//	timeEstimate = TimeSpan.FromSeconds((status.Elapsed.TotalSeconds == 0 ? 1 : status.Elapsed.TotalSeconds) / percentageDone * 100);
					//}
					Console.WriteLine($"{percentageDone}% {args.Data}");
				}
				else
				{
					var header = $"{DateTime.Now:T}:";
					var lines = args.Data.Split(Environment.NewLine).SelectMany(f =>
					{
						return f.Partition(Console.WindowWidth - header.Length - 2).Select(e => new string(e.ToArray()));
					});
					Console.WriteLine($"{header} {string.Join("\n\t", lines)}");
				}
			};

			await process.WaitForExitAsync();

			//await ffMpegArgumentProcessor
			//	.ProcessAsynchronously(true);
		}
	}

	private Regex _progressRegex = new("frame=(.*)fps=(.*)q=(.*)size=(.*)time=(.*)bitrate=(.*)speed=(.*)");

	private void Process_OutputDataReceived(object sender, DataReceivedEventArgs e)
	{
		
	}
}

public ref struct FFmpegStatus
{
	public FFmpegStatus(string[] from)
	{
		Frames = int.Parse(from[0]);
		Fps = double.Parse(from[1]);
		Size = from[3];
		Elapsed = TimeSpan.Parse(from[4], CultureInfo.InvariantCulture);
		Bitrate = from[5];
		Speed = from[6];
	}

	public string Speed { get; set; }
	public string Bitrate { get; set; }
	public TimeSpan Elapsed { get; set; }
	public string Size { get; set; }
	public double Fps { get; set; }
	public int Frames { get; set; }
}

public static class EnumerableExtender
{
	public static bool IsEmpty<T>(this IEnumerable<T> enumerable) => !enumerable?.GetEnumerator()?.MoveNext() ?? true;

	public static IEnumerable<IEnumerable<T>> Partition<T>(this IEnumerable<T> source, int size)
	{
		if (source == null)
			throw new ArgumentNullException(nameof(source));
		if (size < 2)
			throw new ArgumentOutOfRangeException(nameof(size));
		IEnumerable<T> items = source;
		IEnumerable<T> partition;
		while (true)
		{
			partition = items.Take(size);
			if (partition.IsEmpty())
				yield break;
			else
				yield return partition;
			items = items.Skip(size);
		}
	}
}