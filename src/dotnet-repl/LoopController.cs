﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Repl.LineEditorCommands;
using Pocket;
using RadLine;
using Spectre.Console;
using Quit = Microsoft.DotNet.Interactive.Repl.LineEditorCommands.Quit;

namespace Microsoft.DotNet.Interactive.Repl
{
    public class LoopController : IDisposable
    {
        private readonly Kernel _kernel;
        private readonly Action _quit;
        private readonly CompositeDisposable _disposables = new();
        private readonly ManualResetEvent _commandCompleted = new(false);

        private readonly CancellationTokenSource _disposalTokenSource = new();

        private readonly SillyExecutionStatusMessageGenerator _executionStatusMessageGenerator = new();

        private readonly List<SubmitCode> _history = new();

        private readonly LineEditorPrompt _prompt = new(
            "[bold aqua slowblink]  >[/]",
            "[bold aqua slowblink]...[/]");

        public LoopController(
            Kernel kernel,
            Action quit,
            IAnsiConsole? terminal = null,
            IInputSource? inputSource = null)
        {
            _kernel = kernel;
            _quit = quit;

            _disposables.Add(() => _disposalTokenSource.Cancel());

            _kernel.AddMiddleware(async (command, context, next) =>
            {
                await next(command, context);

                if (command is SubmitCode current)
                {
                    TryAddToHistory(current);
                }
            });

            LineEditor = new LineEditor(terminal, inputSource)
            {
                MultiLine = true,
                Prompt = _prompt,
                Completion = new KernelCompletion(kernel),
                Highlighter = ReplWordHighlighter.Create()
            };

            AddKeyBindings(LineEditor);
        }

        public IReadOnlyList<SubmitCode> History => _history;

        public int HistoryIndex { get; internal set; } = -1;

        public LineEditor LineEditor { get; }

        internal string? StashedBufferContent { get; set; }

        public void Start() => Task.Run(RunAsync);

        public bool TryAddToHistory(SubmitCode submitCode)
        {
            if (string.IsNullOrEmpty(submitCode.Code))
            {
                return false;
            }

            if (HistoryIndex < History.Count - 1)
            {
                return false;
            }

            if (History.LastOrDefault() is { } previous)
            {
                if (previous.Code.Equals(submitCode.Code))
                {
                    return false;
                }
            }

            _history.Add(submitCode);
            HistoryIndex = History.Count;
            return true;
        }

        public async Task RunAsync()
        {
            while (!_disposalTokenSource.IsCancellationRequested)
            {
                _commandCompleted.Reset();

                var input = await LineEditor.ReadLine(_disposalTokenSource.Token);

                if (_disposalTokenSource.IsCancellationRequested)
                {
                    return;
                }

                var command = new SubmitCode(input);

                KernelCommandResult? result = default;

                await AnsiConsole.Status().StartAsync(_executionStatusMessageGenerator.GetStatusMessage(), async ctx =>
                {
                    ctx.Spinner(new ClockSpinner());
                    ctx.SpinnerStyle(Style.Parse("green"));

                    result = await _kernel.SendAsync(command);

                    if (result is { })
                    {
                        HandleKernelEvents(command, result, ctx);
                    }
                });
            }
        }

        private void HandleKernelEvents(
            KernelCommand command,
            KernelCommandResult result,
            StatusContext context)
        {
            var events = result.KernelEvents;


            using var _ = events.Subscribe(@event =>
            {
                switch (@event)
                {
                    // events that tell us whether the submission was valid

                    case IncompleteCodeSubmissionReceived incomplete when incomplete.Command == command:
                        break;

                    case CompleteCodeSubmissionReceived complete when complete.Command == command:
                        break;

                    case CodeSubmissionReceived codeSubmissionReceived:
                        break;

                    // output / display events

                    case ErrorProduced errorProduced:
                        RenderErrorEvent((errorProduced));

                        break;

                    case StandardOutputValueProduced standardOutputValueProduced:
                        RenderSuccessfulEvent((standardOutputValueProduced));
                        break;

                    case DisplayedValueProduced displayedValueProduced:
                        RenderSuccessfulEvent((displayedValueProduced));
                        break;

                    case DisplayedValueUpdated displayedValueUpdated:
                        RenderSuccessfulEvent((displayedValueUpdated));
                        break;

                    case ReturnValueProduced returnValueProduced:
                        RenderSuccessfulEvent((returnValueProduced));
                        break;

                    case StandardErrorValueProduced standardErrorValueProduced:
                        RenderErrorEvent((standardErrorValueProduced));
                        break;

                    // command completion events

                    case CommandFailed failed when failed.Command == command:
                        RenderErrorMessage(failed.Message);

                        // if (failed.Exception is { })
                        // {
                        //     AnsiConsole.WriteException(failed.Exception);
                        // }

                        _commandCompleted.Set();

                        break;

                    case CommandSucceeded succeeded when succeeded.Command == command:
                        // RenderSuccessfulOutput(displayText);
                        _commandCompleted.Set();
                        break;
                }
            });

            void RenderSuccessfulEvent(DisplayEvent @event)
            {
                AnsiConsole.Render(
                    new Panel(GetMarkup(@event))
                        .Header("[green]✔[/]")
                        .Expand()
                        .RoundedBorder()
                        .BorderColor(Color.Green));
            }

            void RenderErrorEvent(DisplayEvent @event)
            {
                AnsiConsole.Render(
                    new Panel(GetMarkup(@event))
                        .Header("[red]❌[/]")
                        .Expand()
                        .RoundedBorder()
                        .BorderColor(Color.Red));
            }

            void RenderErrorMessage(string message)
            {
                AnsiConsole.Render(
                    new Panel(Markup.Escape(message))
                        .Header("[red]❌[/]")
                        .Expand()
                        .RoundedBorder()
                        .BorderColor(Color.Red));
            }
        }

        private static Markup GetMarkup(DisplayEvent @event)
        {
            var formattedValue = @event.FormattedValues.First();

            var markup = formattedValue.MimeType switch
            {
                "text/plain+spectre" => new Markup(formattedValue.Value),
                _ => new Markup(Markup.Escape(formattedValue.Value))
            };
            return markup;
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }

        private void AddKeyBindings(LineEditor editor)
        {
            editor.KeyBindings.Add(
                ConsoleKey.C,
                ConsoleModifiers.Control,
                () => new Quit(_quit));

            editor.KeyBindings.Add<Clear>(
                ConsoleKey.C,
                ConsoleModifiers.Control | ConsoleModifiers.Alt);

            editor.KeyBindings.Add(
                ConsoleKey.UpArrow,
                ConsoleModifiers.Control,
                () => new PreviousHistory(this));

            editor.KeyBindings.Add(
                ConsoleKey.DownArrow,
                ConsoleModifiers.Control,
                () => new NextHistory(this));
        }
    }
}