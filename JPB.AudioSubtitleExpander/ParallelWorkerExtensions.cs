using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;

namespace JPB.AudioSubtitleExpander;

public delegate string UpdateText(int processed, int toBeProcessed, int progressPerSec);
public delegate string ProgressItem<in T>(T item);

public static class ParallelWorkerExtensions
{

	public static string ToBytesCount(this long bytes, bool isISO = true)
	{
		int unit = isISO ? 1024 : 1000;
		string unitStr = "B";
		if (bytes < unit)
		{
			return string.Format("{0} {1}", bytes, unitStr);
		}
		int exp = (int)(Math.Log(bytes) / Math.Log(unit));
		return string.Format("{0:##.##} {1}{2}{3}", bytes / Math.Pow(unit, exp), "KMGTPEZY"[exp - 1], isISO ? "i" : "", unitStr);
	}

	public static async Task FindFilesAsync(this BlockingCollection<string> targetCollection,
		string sourceDir)
	{
		string[] extensions = { "mkv", "m4v", "mp4" };

		IEnumerable<string> GetFiles(string directory)
		{
			var searchStack = new Stack<string>();
			searchStack.Push(directory);
			while (searchStack.TryPop(out var dir))
			{
				foreach (var extension in extensions)
				{
					foreach (var enumerateFile in Directory.EnumerateFiles(dir, $"*.{extension}"))
					{
						yield return enumerateFile;
					}
				}
				foreach (var subDir in Directory.EnumerateDirectories(dir))
				{
					searchStack.Push(subDir);
				}
			}
		}
		var sourceFiles = GetFiles(sourceDir)
			.Where(e => extensions.Contains(Path.GetExtension(e).Trim('.').ToLower()));

		await Task.Run(() =>
		{
			foreach (var sourceFile in sourceFiles)
			{
				targetCollection.Add(sourceFile);
			}

			targetCollection.CompleteAdding();
		});
	}

	private class ConversionProgress<T> : IProgress<double>
	{
		public T Value { get; }
		public double Progress { get; private set; }

		public ConversionProgress(T value)
		{
			Value = value;
		}

		public void Report(double value)
		{
			Progress = value;
		}
	}

	private class ConsoleBufferedTextBuilder
	{
		private StringBuilder _builder = new StringBuilder(255);
		private int _width;

		public void SetWidth(int width)
		{
			if (_builder.Length > 0)
			{
				throw new NotSupportedException("cannot change the width while having text in cache");
			}

			_width = width;
		}

		ReadOnlySpan<char> Blank(string line)
		{
			return Blank(line.Length);
		}

		private ReadOnlySpan<char> Blank(int lineLength)
		{
			return string.Join("", Enumerable.Repeat(" ", _width - lineLength));
		}

		public void AppendLine(string text)
		{
			foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
			{
				if (line.Length > _width)
				{
					var lineCounter = 0;
					do
					{
						var targetWidth = lineCounter + _width;
						int lineLength;
						if (targetWidth < line.Length)
						{
							lineLength = targetWidth;
							_builder.Append(line[lineCounter..lineLength]);
						}
						else
						{
							lineLength = lineCounter + line.Length - lineCounter; 
							_builder.Append(line[lineCounter..lineLength])
								.Append(Blank(lineLength - _width));
						}

						lineCounter = lineLength;
					} while (lineCounter < line.Length);
				}
				else if (line.Length == _width)
				{
					_builder.Append(line);
				}
				else
				{
					_builder.Append(line)
						.Append(Blank(line));
				}
			}
		}

		public int Flush()
		{
			var format = _builder.ToString();
			Console.Write(format);
			_builder.Clear();
			return format.Length;
		}
	}

	public static async Task WorkInParallelInteractive<T>(this BlockingCollection<T> items,
		Func<T, IProgress<double>, ValueTask> worker,
		UpdateText updateText,
		ProgressItem<T> progressItemText,
		int workers = 4) where T : notnull
	{
		void AddTask(Dictionary<Thread, (TaskCompletionSource state, bool isWorking)> taskCompletionSources)
		{
			var taskFinished = new TaskCompletionSource();
			var thread = new Thread(() => Run(taskFinished));
			thread.Start();
			taskCompletionSources.Add(thread, (taskFinished, false));
		}

		Console.WriteLine("Press + to add worker or - to remove one.");

		var stopToken = new CancellationTokenSource();
		var processedItems = 0;
		var progress = new ConcurrentDictionary<T, ConversionProgress<T>>();
		var lastTextLength = 0;

		var location = Console.GetCursorPosition();

		var lastProcessedItems = 0;
		var states = new[] { '|', '/', '-', '\\', '|', '/', '-', '\\' };
		var stateCounter = 0;

		var progressTextBuilder = new ConsoleBufferedTextBuilder();
		void UpdateState(int updatesPerSec)
		{
			Console.SetCursorPosition(location.Left, location.Top);
			progressTextBuilder.SetWidth(Console.BufferWidth);



			var mainText = $" {states[stateCounter % states.Length]} " +
			               updateText(processedItems, items.Count, processedItems - lastProcessedItems);
			progressTextBuilder.AppendLine(mainText);

			Console.Title = mainText.Split('\n')[0];
			var conversionProgresses = progress.ToArray();
			foreach (var conversionProgress in conversionProgresses)
			{
				progressTextBuilder.AppendLine($"  - {progressItemText(conversionProgress.Key)} - {conversionProgress.Value.Progress}/100%");
			}

			var textLength = progressTextBuilder.Flush();

			var lengthToBuffer = lastTextLength - textLength;
			if (lengthToBuffer > 0)
			{
				Console.Write(string.Join("", Enumerable.Repeat(" ", lengthToBuffer)));
			}

			lastTextLength = textLength;
			if (stateCounter % updatesPerSec == 0)
			{
				lastProcessedItems = processedItems;
			}
			Console.SetCursorPosition(location.Left, location.Top);
			stateCounter++;
		}

		var tasks = new Dictionary<Thread, (TaskCompletionSource state, bool isWorking)>();
		async void Run(TaskCompletionSource taskCompletionSource)
		{
			var currentThread = Thread.CurrentThread;
			foreach (var item in items.GetConsumingEnumerable())
			{
				var itemProgress = progress.GetOrAdd(item!, (arg => new ConversionProgress<T>(item)));
				await worker(item, itemProgress);
				Interlocked.Add(ref processedItems, 1);
				progress.TryRemove(item, out _);

				if (taskCompletionSource.Task.IsCanceled)
				{
					break;
				}
			}
			taskCompletionSource.SetResult();
			tasks.Remove(currentThread);
		}

		Task.Run(async () =>
		{
			var updatesPerSec = 3;
			while (!stopToken.IsCancellationRequested)
			{
				await Task.Delay(1000 / updatesPerSec);
				UpdateState(updatesPerSec);
			}
			UpdateState(updatesPerSec);
		});

		Task.Run(() =>
		{
			while (!stopToken.IsCancellationRequested)
			{
				var consoleKeyInfo = Console.ReadKey(true);
				switch (consoleKeyInfo.Key)
				{
					case ConsoleKey.Add:
						AddTask(tasks);
						break;
					case ConsoleKey.Subtract when tasks.Count > 1:
						tasks.Last().Value.state.SetCanceled();
						break;
				}
			}
		});

		for (int i = 0; i < workers; i++)
		{
			AddTask(tasks);
		}

		do
		{
			await Task.WhenAll(tasks.Select(e => e.Value.state.Task));
		} while (tasks.Any(e => e.Value.state.Task.Status == TaskStatus.Running));

		await Task.Delay(1001);
		stopToken.Cancel();
	}

	public static string Truncate(string source, int length, string ellipsis = "...")
	{
		ellipsis = ellipsis ?? "...";
		if (string.IsNullOrEmpty(source))
		{
			return string.Empty;
		}
		int lMinusTruncate = length - ellipsis.Length;
		if (source.Length > length)
		{
			var builder = new StringBuilder(length + ellipsis.Length);
			builder.Append(source[..(lMinusTruncate < 0 ? 0 : lMinusTruncate)]);
			builder.Append(ellipsis);
			return builder.ToString();

		}
		return source;
	}
}