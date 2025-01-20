using System.Collections;
using Polly;
using System.Collections.Concurrent;
using System.Text.Json;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Exceptions;
using FFMpegCore.Pipes;
using Morestachio;
using Morestachio.Document;
using Morestachio.Framework.Context;
using Morestachio.Framework.Expression;
using Morestachio.Framework.Expression.Framework;
using Morestachio.Framework.Expression.Parser;
using Instances;

namespace JPB.AudioSubtitleExpander;

public class MeasureSubStreams : WorkerBase
{
	public class MediaStatisticInfo
	{
		private long _size;
		private long _counts;
		public string Type { get; set; }

		public long Size => _size;

		public long Counts => _counts;

		public void AddTo(long size)
		{
			Interlocked.Add(ref _counts, 1);
			Interlocked.Add(ref _size, size);
		}
	}

	public override async Task Run()
	{
		Console.WriteLine("Input directory:");
		var sourceDir = GetInput("ASE_SourceDirectory");
		if (!Directory.Exists(sourceDir))
		{
			Console.WriteLine("Directory does not exist.");
			return;
		}

		//Console.WriteLine("Approximate size when NUMBER_OF_BYTES tag is missing? (y|n)");
		//var approximateSize = GetInput("ASE_Measure_ApproxSize") == "y";

		var mediaElements = new BlockingCollection<string>();
		var findFilesAsync = mediaElements.FindFilesAsync(sourceDir);

		var evaluated = new ConcurrentDictionary<string, long>();
		if (File.Exists("items.json"))
		{
			foreach (var valueTuple in JsonSerializer.Deserialize<(string path, long size)[]>(await File.ReadAllBytesAsync("items.json")) ?? throw new InvalidOperationException())
			{
				evaluated.TryAdd(valueTuple.path, valueTuple.size);
			}
		}

		long StreamSize(IMediaAnalysis sourceFile, MediaStream context)
		{
			var taggedStreamSize = context.Tags.Where(e => e.Key.StartsWith("NUMBER_OF_BYTES"))
				.Where(e => long.TryParse(e.Value, out _))
				.Where(sizeText => !sizeText.Equals(default) && !string.IsNullOrEmpty(sizeText.Value))
				.Sum(sizeText => long.Parse(sizeText.Value));
			if (taggedStreamSize != 0)
			{
				return taggedStreamSize;
			}

			var duration = context.Duration;

			if (duration == TimeSpan.Zero)
			{
				duration = sourceFile.Duration;
			}

			switch (context)
			{
				case AudioStream audioStream:
					return (long)(duration.TotalSeconds * (audioStream.BitRate * (audioStream.BitDepth ?? 8) / 8) * audioStream.Channels);
				case VideoStream videoStream:
					return (long)(duration.TotalSeconds * (videoStream.AverageFrameRate == 0 ? videoStream.AvgFrameRate : videoStream.AverageFrameRate) 
					                                    * (videoStream.BitRate * (videoStream.BitDepth ?? 8) / 8)
					                                    * videoStream.Width * videoStream.Height);
				case SubtitleStream subtitleStream:
					return (long)(duration.TotalSeconds * (subtitleStream.BitRate * (subtitleStream.BitDepth ?? 8) / 8));
			}

			return 0;

		}

		var streamStats = new ConcurrentDictionary<string, MediaStatisticInfo>();

		Console.Write("Property Selectors:");
		var statSelectors = GetInput("ASE_Measure_Selectors").Split(';').Select(e => e.Trim()).ToArray();

		var selectors = new List<Func<string, IMediaAnalysis, Task>>();

		var parserOptions = ParserOptionsBuilder.New()
			.Build();
		foreach (var statSelector in statSelectors)
		{
			var parts = statSelector.Split(':');
			var key = parts[0];
			var data = parts[1];
			var dataValue = parts[2];
			var dataExpression = ExpressionParser.ParseExpression(data, TokenzierContext.FromText(dataValue)).Expression
				.Compile(parserOptions);
			
			
			var dataValueExpression = ExpressionParser.ParseExpression(dataValue, TokenzierContext.FromText(dataValue)).Expression
				.Compile(parserOptions);
			selectors.Add(async (sourceFile, analysis) =>
			{
				var mediaStreamContext =
					await dataExpression(new ContextObject(".", null, analysis), new ScopeData(parserOptions));

				if (mediaStreamContext.Value is IEnumerable<MediaStream> streams)
				{
					foreach (var item in streams)
					{
						var size = StreamSize(analysis, item);
						var dataValue = await dataValueExpression(new ContextObject(".", mediaStreamContext, item), new ScopeData(parserOptions));
						var mediaStatisticInfo = streamStats.GetOrAdd(dataValue.Value.ToString(), s => new MediaStatisticInfo()
						{
							Type = s
						});
						mediaStatisticInfo.AddTo(size);
					}
				}
				else if (mediaStreamContext.Value is MediaStream mediaStream)
				{
					var size = StreamSize(analysis, mediaStream);
					var dataValue = await dataValueExpression(new ContextObject(".", mediaStreamContext, mediaStreamContext.Value), new ScopeData(parserOptions));
					var mediaStatisticInfo = streamStats.GetOrAdd(dataValue.Value.ToString(), s => new MediaStatisticInfo()
					{
						Type = s
					});
					mediaStatisticInfo.AddTo(size);
				}
			});
		}

		var totalSize = evaluated.Select(e => e.Value).Sum();

		async ValueTask RunEval(string match, IProgress<double> progress)
		{
			if (evaluated.ContainsKey(match))
			{
				return;
			}

			var sourceMediaInfoResult = await Policy.Handle<Exception>().Or<FFMpegException>()
				.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(5 + i))
				.ExecuteAndCaptureAsync(() => FFProbe.AnalyseAsync(match));

			if (sourceMediaInfoResult.FaultType is not null)
			{
				return;
			}

			var sourceMediaInfo = sourceMediaInfoResult.Result;

			foreach (var selector in selectors)
			{
				await selector(match, sourceMediaInfo);
			}
			evaluated.TryAdd(match, 0);
			Interlocked.Add(ref totalSize, 0);
		}

		await mediaElements.WorkInParallelInteractive(RunEval,
			(processed, toBeProcessed, persec) =>
				$"Size: {totalSize.ToBytesCount(true)} | {processed}/{(!mediaElements.IsAddingCompleted ? "~" : "")}{toBeProcessed + processed}/ {persec}-sec {Environment.NewLine}" +
				$"{string.Join(", ", streamStats.Select(e => $"{e.Value.Type}={e.Value.Counts}|{e.Value.Size.ToBytesCount()}"))}",
			Path.GetFileName, 1);
		await findFilesAsync;
	}
}