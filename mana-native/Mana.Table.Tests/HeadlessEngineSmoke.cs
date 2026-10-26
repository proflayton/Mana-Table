using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mana.Contracts;
using Mana.Forge;
using Mana.Magic;
using Mana.Table.Interaction;
using Mana.Table.Presentation;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
using Point = Microsoft.Xna.Framework.Point;

namespace Mana.Table;

/// <summary>A short real-Forge game observed and operated through production composition, without a window.</summary>
internal static class HeadlessEngineSmoke
{
    internal static async Task RunAsync(ForgePaths paths)
    {
        Directory.CreateDirectory(paths.Profile);
        var checks = new List<string>();
        var frames = new List<object>();
        var actions = new List<object>();
        var elapsed = Stopwatch.StartNew();
        var json = new JsonSerializerOptions { WriteIndented = true };
        string? error = null;
        int preparedUpdates = 0;
        double updateMilliseconds = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException(description);
            checks.Add(description);
            File.AppendAllText(Path.Combine(paths.Profile, "headless-engine-progress.log"), description + "\n");
        }
        await using var engine = new ForgeEngine(paths);
        try {
            await engine.InitializeAsync();
            var deck = await engine.ImportAsync("Headless production composition", "Deck\n99 Forest\nCommander\n1 Rhys the Redeemed");
            var setup = await engine.PrepareSoloAsync(deck.Id);
            Check(setup.Opponents.Any(opponent => opponent.Id == "green") && setup.Opponents.Any(opponent => opponent.Id == "red"),
                "The real adapter imports the Commander deck and exposes bundled starter AI opponents");
            var deckBeforeMetadata = await engine.OpenDeckAsync(deck.Id);
            var leader = deckBeforeMetadata.Entries.Single(entry => entry.Section == "Commander");
            var candidate = deckBeforeMetadata.Entries.First(entry => entry.Section == "Main");
            var workshop = await engine.WorkshopAsync(deck.Id, deckBeforeMetadata.Revision);
            string playableVersion = workshop.VersionId;
            var annotated = await engine.UpdateWorkshopAsync(deck.Id, deckBeforeMetadata.Revision, new(workshop.MetadataRevision,
                Roles: [new(leader.Id, ["Tokens", "Payoff"], true, "local")],
                Considering: [new(candidate.Id, 1, ["Ramp", "Patch regression"])]));
            var planOnly = await engine.UpdateWorkshopAsync(deck.Id, deckBeforeMetadata.Revision,
                new(annotated.MetadataRevision, Plan: "Check omission and explicit clearing without changing the played list."));
            Check(planOnly.Plan.StartsWith("Check omission", StringComparison.Ordinal)
                && planOnly.Roles.Any(role => role.CardId == leader.Id && role.Pinned && role.Origin == "local"
                    && role.Roles.SequenceEqual(["Tokens", "Payoff"]))
                && planOnly.Considering is [{ CardId: var consideredId, Quantity: 1 } considered] && consideredId == candidate.Id
                && considered.Categories.SequenceEqual(["Ramp", "Patch regression"])
                && annotated.VersionId == playableVersion && planOnly.VersionId == playableVersion,
                "A real plan-only metadata patch preserves omitted roles, pins and Considering categories without changing the playable version");
            var cleared = await engine.UpdateWorkshopAsync(deck.Id, deckBeforeMetadata.Revision, new(planOnly.MetadataRevision,
                Roles: ImmutableArray<WorkshopRole>.Empty, Considering: ImmutableArray<ConsideringEdit>.Empty));
            Check(!cleared.Roles.Any(role => role.Pinned || role.Origin == "local") && cleared.Considering.IsEmpty
                && cleared.Plan == planOnly.Plan && cleared.VersionId == playableVersion,
                "Explicit empty immutable metadata collections clear local roles, pins and Considering while preserving the plan and playable version");
            var afterMetadata = await engine.OpenDeckAsync(deck.Id);
            Check(afterMetadata.Name == deckBeforeMetadata.Name && afterMetadata.Format == deckBeforeMetadata.Format
                && afterMetadata.Entries.Select(entry => (entry.Section, entry.Id, entry.Quantity))
                    .SequenceEqual(deckBeforeMetadata.Entries.Select(entry => (entry.Section, entry.Id, entry.Quantity))),
                "Metadata patch omission and clearing preserve every section, exact printing and quantity used by the ensuing real game");
            var withHistory = await engine.RenameDeckAsync(deck.Id, afterMetadata.Revision, "Headless setup history");
            var practice = await engine.PracticeAsync(deck.Id, withHistory.Revision, "new");
            var firstSetup = await engine.PrepareSoloAsync(deck.Id);
            var repeatedSetup = await engine.PrepareSoloAsync(deck.Id);
            var retained = await engine.WorkshopAsync(deck.Id, withHistory.Revision);
            var continuedPractice = await engine.PracticeAsync(deck.Id, withHistory.Revision, "draw");
            Check(firstSetup.DeckId == deck.Id && repeatedSetup.DeckId == deck.Id && repeatedSetup.DeckName == withHistory.Name
                && retained.Revision == withHistory.Revision && continuedPractice.TestId == practice.TestId
                && continuedPractice.Draws == 1 && continuedPractice.Hand.Length == practice.Hand.Length + 1,
                "Repeated explicit setup reuses the same real editor revision and active practice sample");
            var undoneSetup = await engine.UndoDeckAsync(deck.Id, withHistory.Revision);
            Check(withHistory.CanUndo && undoneSetup.Name == afterMetadata.Name,
                "Repeated setup preserves the existing editor undo history rather than reopening the saved deck");
            var alternate = await engine.DuplicateDeckAsync(deck.Id, undoneSetup.Revision, "Other explicit setup deck");
            var originalSetup = await engine.PrepareSoloAsync(deck.Id);
            var preparedOriginal = await engine.WorkshopAsync(deck.Id, alternate.Revision);
            var switchedSetup = await engine.PrepareSoloAsync(alternate.Id);
            var switchedOwner = await engine.WorkshopAsync(alternate.Id, alternate.Revision);
            bool wrongOwnerRejected = false;
            try { await engine.RenameDeckAsync(deck.Id, switchedOwner.Revision, "Wrong owner must not be renamed"); }
            catch (EngineCommandRejectedException) { wrongOwnerRejected = true; }
            Check(originalSetup.DeckId == deck.Id && originalSetup.DeckName == afterMetadata.Name
                && switchedSetup.DeckId == alternate.Id && switchedSetup.DeckName == alternate.Name
                && preparedOriginal.Revision == switchedOwner.Revision && wrongOwnerRejected,
                "Switching explicit setup reports its actual deck association and rejects a same-revision write for the previous owner");
            Check((await engine.PrepareSoloAsync(deck.Id)).DeckId == deck.Id,
                "Explicit setup can return to the requested original deck before the real match starts");
            await AiFrameworkEngineCheck.RunAsync(engine, deck.Id, Check);
            await engine.StartSoloAsync(deck.Id, ["green", "red", "green"]);
            GameSnapshot? initial = null;
            while (initial?.Players.Length != 4 || initial.Decision == null) {
                Guard();
                initial = await engine.ObserveAsync();
                if (initial?.Status is "error" or "finished") throw new InvalidOperationException(initial.Error ?? "Game ended before its opening decision.");
                if (initial?.Decision == null) await Task.Delay(10);
            }
            Check(initial.Players.All(player => player.Life == 40), "A real four-player Commander game reaches its opening decision");
            new PlayPreferences { Automatic = false, ReducedMotion = true, Sounds = false, TextScale = 1.3f }.Save(paths.Profile);
            using var app = new TableApplication(TestTableServices.For(engine), paths.Profile, initialState: initial);
            app.Automated = true; app.Initialize();
            var view = app.TestPort;
            view.SetTableStudy(true);
            var viewport = new Point(1600, 900);
            Point pointer = new(20, 60);
            double seconds = 0;
            PresentationFrame frame = Tick("opening");
            Save("opening");
            string? landId = null, commanderId = null;
            bool landSaved = false, commanderSaved = false, openingHandRead = false, bottomHandRead = false, commanderPayment = false, paidWithForest = false;
            int decisions = 0, mulligans = 0;
            bool freeMulliganObserved = false;

            while (!landSaved || !commanderSaved) {
                Guard();
                await Refresh("observe");
                var state = view.Match ?? throw new InvalidOperationException("The active Forge game disappeared.");
                if (state.Status is "error" or "finished") throw new InvalidOperationException(state.Error ?? "Game ended before the focused scenario completed.");
                var land = state.Viewer!.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Forest");
                var commander = state.Viewer.Zone("Battlefield").Cards.FirstOrDefault(card => card.Name == "Rhys the Redeemed");
                if (land != null && !landSaved) {
                    landId = CardViews.Identity(land); landSaved = true; Save("land-on-battlefield");
                    Check(frame.Cards.Any(card => card.Id == landId && card.Zone == "Battlefield")
                        && state.Viewer.LandPlaysUsed >= 1,
                        "Forge's resolved land play reaches the production scene and authoritative land-play state");
                }
                if (commander != null && !commanderSaved) {
                    commanderId = CardViews.Identity(commander); commanderSaved = true; Save("commander-on-battlefield");
                    var displayed = frame.Cards.Single(card => card.Id == commanderId && card.Zone == "Battlefield");
                    Check(displayed.Card.Power == commander.Power && displayed.Card.Toughness == commander.Toughness
                        && commander.Power == 1 && commander.Toughness == 1,
                        "The actual paid commander resolves into the production scene with its current one power and one toughness");
                    Check(commanderPayment && paidWithForest && state.Viewer.Zone("Battlefield").Cards.Any(card => card.Name == "Forest" && card.Tapped)
                        && state.Viewer.Mana.Values.All(amount => amount == 0) && !state.Viewer.Zone("Command").Cards.Any(card => card.VisualId == commander.VisualId),
                        "The real command-zone cast paid through its Forest source, spent its mana and left the command zone before resolving");
                }
                if (landSaved && commanderSaved) break;
                if (view.Busy || view.Match?.Decision == null) { await Task.Delay(5); continue; }
                if (++decisions > 80) throw new InvalidOperationException("The focused opening scenario required unexpectedly many decisions.");
                var decision = view.Match.Decision;
                if (mulligans == 1 && state.Turn == 0) {
                    Check(decision is { Payload: OpeningHandPayload }
                            && view.Match.Viewer!.Zone("Hand").Count == 7,
                        "The first real multiplayer mulligan redraws seven and returns to Keep without a bottom-card prompt");
                    freeMulliganObserved = true;
                }
                if (decision is { Payload: PaymentPayload } && !paidWithForest) {
                    Check(decision.Context.Source?.Card?.Name == "Rhys the Redeemed" && decision.Context.Message.Length > 0,
                        "Dragging the real paid commander opens its authoritative mana-payment input with the current source and cost instruction");
                    commanderPayment = true; Save("commander-payment");
                    var source = view.Match.Viewer!.Zone("Battlefield").Cards.First(card => card.Name == "Forest" && !card.Tapped);
                    await PlayCard(source, "Battlefield");
                    Save("after-forest-action-receipt");
                    var paymentDeadline = Stopwatch.StartNew();
                    while (!view.Match!.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == source.VisualId && card.Tapped)
                        && paymentDeadline.Elapsed < TimeSpan.FromSeconds(5)) {
                        Guard(); await Task.Delay(5); await Refresh("await-authoritative-forest-tap");
                    }
                    Save("forest-payment-observed");
                    Check(view.Match!.Viewer!.Zone("Battlefield").Cards.Any(card => card.VisualId == source.VisualId && card.Tapped),
                        "The actual Forest mana action taps its source while paying for the commander");
                    paidWithForest = true;
                    continue;
                }
                if (!openingHandRead && decision is { Payload: OpeningHandPayload }) {
                    var openingCard = view.Match.Viewer!.Zone("Hand").Cards.First();
                    string game = view.Match.Id, prompt = decision.Id;
                    Click("card:" + openingCard.Key + ":" + openingCard.VisualId);
                    frame = Tick("opening-hand-reader"); frame = Tick("opening-hand-reader-settled");
                    Check(view.Overlay.Length == 0 && frame.Cards.Any(card => card.Id == CardViews.Identity(openingCard)
                            && card.Zone == "Hand" && card.Focused)
                            && frame.Hits.Any(hit => hit.Id == "confirm") && frame.Hits.Any(hit => hit.Id == "cancel")
                            && view.Match.Id == game && view.Match.Decision?.Id == prompt && !view.Busy,
                        "A real Forge keep/mulligan hand card opens the normal reader while both decision controls remain available and no command is sent");
                    Save("keep-hand-reader");
                    Click("card:" + openingCard.Key + ":" + openingCard.VisualId);
                    Check(!frame.Cards.Any(card => card.Focused) && view.Overlay.Length == 0 && view.Match.Decision?.Id == prompt,
                        "A second opening-card click collapses reading without keeping or redrawing the real hand");
                    openingHandRead = true;
                }
                if (decision is { Payload: OpeningHandPayload } && mulligans < 2) {
                    Click("cancel"); mulligans++;
                    await Idle();
                    continue;
                }
                if (!bottomHandRead && mulligans == 2 && state.Turn == 0
                        && decision is { Payload: EntitySelectionPayload { Complete.Enabled: false } }) {
                    Check(decision.Payload is EntitySelectionPayload { Range: { Min: 1, Max: 1 } },
                        "The second multiplayer mulligan projects exactly one required bottom card through the native provider");
                    var bottomCard = view.Match.Viewer!.Zone("Hand").Cards.First(card => card.Selectable);
                    string prompt = decision.Id;
                    int count = view.Match.Viewer.Zone("Hand").Count;
                    Click("card:" + bottomCard.Key + ":" + bottomCard.VisualId);
                    frame = Tick("bottom-hand-reader"); frame = Tick("bottom-hand-reader-settled");
                    Check(frame.Cards.Any(card => card.Id == CardViews.Identity(bottomCard) && card.Focused)
                            && frame.Hits.Any(hit => hit.Id == "read-card-action") && view.Match.Decision?.Id == prompt && !view.Busy
                            && view.Match.Viewer!.Zone("Hand").Count == count && !view.Match.Viewer.Zone("Hand").Cards.Any(card => card.Highlighted),
                        "After two real Commander mulligans, clicking a bottom-card candidate only enlarges it and leaves the hand and selection unchanged");
                    Save("bottom-hand-reader");
                    Click("read-card-action"); await Idle();
                    Save("bottom-hand-selection-observed");
                    Check(view.Match!.Decision is { Payload: EntitySelectionPayload { Complete.Enabled: true } }
                            && view.Match.Viewer!.Zone("Hand").Count == count
                            && view.Match.Viewer.Zone("Hand").Cards.Any(card => card.VisualId == bottomCard.VisualId && card.Highlighted),
                        "The explicit reader choice selects the real London bottom card without removing it before final confirmation");
                    Save("bottom-hand-selected");
                    Click("confirm"); await Idle();
                    var bottomDeadline = Stopwatch.StartNew();
                    while (view.Match!.Viewer!.Zone("Hand").Cards.Any(card => card.VisualId == bottomCard.VisualId)
                            && bottomDeadline.Elapsed < TimeSpan.FromSeconds(5)) {
                        Guard(); await Task.Delay(5); await Refresh("await-bottom-card-departure");
                    }
                    Check(!view.Match!.Viewer!.Zone("Hand").Cards.Any(card => card.VisualId == bottomCard.VisualId),
                        "Only final confirmation removes the real selected card from the kept hand");
                    bottomHandRead = true;
                    continue;
                }
                if (decision is { Payload: PriorityPayload }
                    && view.Match.ActivePlayerId == view.Match.ViewerId && view.Match.PhaseKey is "MAIN1" or "MAIN2") {
                    Card? playable = !landSaved ? view.Match.Viewer!.Zone("Hand").Cards.FirstOrDefault(card => card.Selectable && card.Type.Contains("Land", StringComparison.Ordinal))
                        : !commanderSaved ? view.Match.Viewer!.Zone("Command").Cards.FirstOrDefault(card => card.Selectable) : null;
                    if (playable != null) {
                        await PlayCard(playable, landSaved ? "Command" : "Hand"); continue;
                    }
                }
                var presentation = DecisionDockPresentation.From(view.Match, new());
                if (presentation.Selection != null || decision.Payload is RevealPayload) {
                    int required = presentation.Selection is { } selection ? Math.Max(selection.Range.Min, Math.Min(1, selection.Range.Max)) : 0;
                    foreach (var choice in presentation.Selection?.Choices.Take(required) ?? []) Click("choice:" + choice.Index);
                    if (frame.Hits.Any(hit => hit.Id == "choicesdone")) Click("choicesdone");
                    else if (frame.Hits.Any(hit => hit.Id == "confirm")) Click("confirm");
                    else throw new InvalidOperationException("No real confirmation control for " + decision.Payload.GetType().Name);
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "confirm projected choice", required });
                } else if (decision.Payload is NumberPayload or TextPayload && frame.Hits.Any(hit => hit.Id == "value")) {
                    Click("value");
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "confirm initial projected value" });
                } else if (DecisionCommands.Confirm(view.Match) != null && frame.Hits.Any(hit => hit.Id == "confirm")) {
                    Click("confirm");
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "confirm current opening/priority decision" });
                } else if (StateClarity.Recipients(view.Match) is { Length: > 0 } recipients) {
                    int recipient = recipients.Any(player => player.Id == view.Match.ViewerId) ? view.Match.ViewerId : recipients[0].Id;
                    var target = frame.Hits.FirstOrDefault(hit => hit.Id == "recipient:" + recipient)
                        ?? frame.Hits.FirstOrDefault(hit => hit.Id == "player:" + recipient)
                        ?? throw new InvalidOperationException("No production control for the projected player choice.");
                    Click(target.Id);
                    actions.Add(new { mode = "application-pointer", decision = decision.Id, action = "choose projected player", recipient });
                } else throw new InvalidOperationException("Unhandled real opening decision: " + JsonSerializer.Serialize(decision));
                await Idle();
            }

            Check(actions.Any(action => JsonSerializer.Serialize(action).Contains("reader-face-drag"))
                && actions.Any(action => JsonSerializer.Serialize(action).Contains("read-card-action"))
                && actions.Any(action => JsonSerializer.Serialize(action).Contains("command-zone-drag"))
                && landId != null && commanderId != null,
                "The real land reader drag, command-zone commander drag and Forest payment action all use production pointer controls");
            Check(openingHandRead, "The real Forge walkthrough exercised opening-hand browsing before explicitly keeping");
            Check(freeMulliganObserved && mulligans == 2 && bottomHandRead, "The same real Forge walkthrough exercised the free multiplayer redraw, inspection, explicit bottom-card selection and confirmation after Commander mulligans");
            Check(frame.Stamp.GameId == view.Match!.Id && frame.Stamp.Revision == view.Match.Revision
                && frame.Stamp.DecisionId == view.Match.Decision?.Id,
                "The published frame is stamped with the exact authoritative game, revision and decision being displayed");
            Check(view.Match.Players.Where(player => player.Id != view.Match.ViewerId).All(player => player.Zone("Hand").Cards.Length == 0)
                && frame.Cards.Where(card => card.Zone == "Hand").All(card => card.Request.Owner == view.Match.ViewerId),
                "Private AI hands remain absent from both the real adapter snapshot and production prepared frame");
            Click("concede"); Click("confirm-concede"); await Idle(); await Refresh("finished");
            Check(view.Match is { Status: "finished", Result: "Defeat" }, "The real game concedes through production confirmation controls and publishes its result");
            Save("finished");
            await AiWholeSeatEngineCheck.RunAsync(engine, paths.Profile, Check);

            GameTime Time() => new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(1.0 / 60));
            PresentationFrame Tick(string stage, ButtonState left = ButtonState.Released)
            {
                seconds += 1.0 / 60;
                var timer = Stopwatch.StartNew();
                app.Update(Time(), new(pointer.X, pointer.Y, 0, left, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released), new(), viewport);
                var composed = app.ReadPresentation(); timer.Stop();
                preparedUpdates++; updateMilliseconds += timer.Elapsed.TotalMilliseconds;
                if (view.Error.Length > 0) throw new InvalidOperationException(view.Error);
                if (composed.Stamp.Revision != (view.Match?.Revision ?? 0)) throw new InvalidOperationException("Composition stamp does not match the actual snapshot.");
                if (view.Match is { } state && state.Players.Any(player => player.Id != state.ViewerId && player.Zone("Hand").Cards.Length > 0))
                    throw new InvalidOperationException("The adapter exposed a private AI hand.");
                frames.Add(new { stage, stamp = composed.Stamp, commandCount = composed.Drawing.Commands.Count,
                    cardCount = composed.Cards.Count, hitCount = composed.Hits.Count });
                return composed;
            }
            async Task Refresh(string stage)
            {
                if (await engine.ObserveAsync() is { } next) view.Accept(next);
                frame = Tick(stage);
            }
            async Task Idle()
            {
                while (view.Busy) { Guard(); await Task.Delay(5); frame = Tick("await-command"); }
                await Refresh("after-command");
            }
            void Save(string stage) => File.WriteAllText(Path.Combine(paths.Profile, "headless-engine-" + stage + ".json"),
                JsonSerializer.Serialize(new { snapshot = view.Match, frame = frame.Observe() }, json));
            void Click(string id)
            {
                var hit = frame.Hits.LastOrDefault(hit => hit.Id == id) ?? throw new InvalidOperationException("Missing real application control: " + id);
                pointer = Exposed(hit);
                frame = Tick("press-" + id, ButtonState.Pressed);
                frame = Tick("release-" + id);
            }
            Point Exposed(TableHit hit)
            {
                bool Matches(Point point) => point.X >= 0 && point.Y >= 0 && point.X < viewport.X && point.Y < viewport.Y
                    && view.HitAt(point) is { } current && current.Id == hit.Id && current.Scope == hit.Scope;
                var center = new Point(Math.Clamp(hit.Bounds.Center.X, 0, viewport.X - 1), Math.Clamp(hit.Bounds.Center.Y, 0, viewport.Y - 1));
                if (Matches(center)) return center;
                for (int y = Math.Max(0, hit.Bounds.Top); y < Math.Min(viewport.Y, hit.Bounds.Bottom); y++)
                    for (int x = Math.Max(0, hit.Bounds.Left); x < Math.Min(viewport.X, hit.Bounds.Right); x++) {
                        var point = new Point(x, y); if (Matches(point)) return point;
                    }
                throw new InvalidOperationException("No exposed real pointer target for " + hit.Id);
            }
            async Task PlayCard(Card card, string sourceZone)
            {
                string occurrence = CardViews.Identity(card);
                var beforeQuery = view.Match!;
                string prompt = beforeQuery.Decision!.Id;
                Check(engine.SupportsSourceActions, "The real rules adapter negotiates engine-owned source actions");
                var sourceActions = await engine.SourceActionsAsync(beforeQuery.Id, prompt, card.Key);
                var repeated = await engine.SourceActionsAsync(beforeQuery.Id, prompt, card.Key);
                var afterQuery = await engine.ObserveAsync() ?? throw new InvalidOperationException("The source action read lost the game.");
                string expectedKind = sourceZone == "Battlefield" ? "activate" : card.Type.Contains("Land", StringComparison.Ordinal) ? "play" : "cast";
                Check(sourceActions.GameId == beforeQuery.Id && sourceActions.DecisionId == prompt
                    && sourceActions.SourceCardKey == card.Key && sourceActions.SourceCardId == card.VisualId
                    && sourceActions.Actions.Any(action => action.Kind == expectedKind && !string.IsNullOrWhiteSpace(action.Text))
                    && sourceActions.Actions.SequenceEqual(repeated.Actions)
                    && afterQuery.Decision?.Id == prompt && afterQuery.Revision == beforeQuery.Revision
                    && afterQuery.Viewer!.Zones.SelectMany(zone => zone.Cards).Single(item => item.VisualId == card.VisualId).Key == card.Key,
                    "Reading the real source actions twice preserves full engine text, stable opaque handles and the unanswered prompt: " + card.Name);
                bool staleQueryRejected = false;
                try { await engine.SourceActionsAsync(beforeQuery.Id, prompt, "expired-source-key"); }
                catch (EngineCommandRejectedException) { staleQueryRejected = true; }
                Check(staleQueryRejected && (await engine.ObserveAsync())?.Decision?.Id == prompt,
                    "A stale source query is rejected without consuming the current game decision");
                var entry = frame.Cards.Single(item => item.Id == occurrence);
                var hit = frame.Hits.Last(item => item.Id == entry.Hit.Id && item.Scope == entry.Hit.Scope);
                Click(hit.Id);
                frame = Tick("restore-reader-footprint"); frame = Tick("reader-settled");
                if (!frame.Cards.Any(item => item.Id == occurrence && item.Focused)) throw new InvalidOperationException("The real card reader did not open.");
                while (!frame.Hits.Any(item => item.Id == "read-card-action")) {
                    Guard(); await Task.Delay(1); frame = Tick("await-engine-source-actions");
                }
                string decisionId = view.Match!.Decision!.Id;
                Check(view.Match.Id == beforeQuery.Id && decisionId == prompt && !view.Busy
                    && frame.Hits.Any(hit => hit.Id == "read-card-action"),
                    "The actual reader exposes its current " + expectedKind + " action without answering the decision: " + card.Name);
                if (expectedKind == "play") {
                    var reader = frame.Cards.Single(item => item.Id == occurrence && item.Focused);
                    pointer = reader.Display.Center.ToPoint();
                    Check(view.HitAt(pointer)?.Id == reader.Hit.Id,
                        "The real land's enlarged face is directly available as the drag source");
                    frame = Tick("press-real-land-reader", ButtonState.Pressed);
                    pointer += new Point(80, 0);
                    Check(reader.Display.Contains(pointer), "The intended land drop remains inside the old enlarged reader bounds");
                    frame = Tick("drag-real-land-reader", ButtonState.Pressed);
                    Check(!frame.Cards.Any(item => item.Focused) && view.DisplayedPose(occurrence) is { } held
                        && held.Size == new Vector2(132, 185) && held.Center == pointer.ToVector2() + new Vector2(0, -72)
                        && view.Match!.Decision?.Id == decisionId,
                        "Dragging the real land shrinks it into a held card above the pointer without answering the decision before release");
                    Save("land-held-from-reader");
                    frame = Tick("release-real-land-reader");
                    Check(app.AcceptedDrag, "Releasing the real land inside its old reader area commits the actual hand drag");
                    actions.Add(new { mode = "application-pointer", decision = decisionId, action = "reader-face-drag", card.Name, occurrence,
                        sourceKey = card.Key, drop = pointer, offeredActions = sourceActions.Actions });
                } else if (sourceZone == "Command") {
                    Click("card:" + card.Key + ":" + card.VisualId);
                    var command = frame.Cards.Single(item => item.Id == occurrence && item.Zone == "Command");
                    pointer = Exposed(frame.Hits.Last(item => item.Id == command.Hit.Id && item.Scope == command.Hit.Scope));
                    frame = Tick("press-real-command-zone", ButtonState.Pressed);
                    pointer = new(800, 420); frame = Tick("drag-real-commander", ButtonState.Pressed);
                    Check(view.Match!.Decision?.Id == decisionId && !view.Busy && view.DisplayedPose(occurrence) is { Size.X: 132 },
                        "The real commander stays uncommitted while held from the command zone over the table");
                    Save("commander-held-from-command"); frame = Tick("release-real-commander");
                    Check(app.AcceptedDrag, "Releasing the real command-zone commander commits its current scoped drag");
                    actions.Add(new { mode = "application-pointer", decision = decisionId, action = "command-zone-drag", card.Name, occurrence,
                        sourceKey = card.Key, drop = pointer, offeredActions = sourceActions.Actions });
                } else {
                    Click("read-card-action");
                    actions.Add(new { mode = "application-pointer", decision = decisionId, action = "read-card-action", card.Name, occurrence,
                        offeredActions = sourceActions.Actions });
                }
                await Idle();
                Check(view.Match?.Decision?.Payload is not AbilityChoicePayload,
                    "The real card gesture proceeds directly without a second generic ability choice: " + card.Name);
            }
        } catch (Exception exception) {
            error = exception.ToString(); throw;
        } finally {
            File.WriteAllText(Path.Combine(paths.Profile, "headless-engine-trace.json"), JsonSerializer.Serialize(new { frames, actions }, json));
            File.WriteAllText(Path.Combine(paths.Profile, "headless-engine-results.json"), JsonSerializer.Serialize(new {
                passed = error == null, checks, error, elapsedSeconds = elapsed.Elapsed.TotalSeconds,
                preparedUpdates, updateMilliseconds,
                meanUpdateMilliseconds = preparedUpdates > 0 ? updateMilliseconds / preparedUpdates : 0,
                scope = "Real Forge game and production application pointer/composition paths; no window or GPU, no simulated Magic rules."
            }, json));
        }
        void Guard()
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(90)) throw new TimeoutException("Real Forge opening-to-land/commander presentation exceeded its bounded smoke watchdog.");
        }
    }
}
