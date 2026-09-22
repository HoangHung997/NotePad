using H2AgentLab.Integration;
using H2AgentLab.Office;
using H2AgentLab.OfficeProtocol;
using H2AgentLab.Verification;

internal static class H2WordCvRepairTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Word CV run boundaries preserve exact Unicode text and reject ambiguous structure", () =>
        {
            const string prefix = "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p>";
            const string suffix = "</w:p></w:body></w:document>";
            var xml = prefix + "<w:r><w:t>Giới thiệu 😀</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:tab/><w:t>CV</w:t></w:r>" + suffix;
            Check(WordRunBoundaries.TryRead(xml, "Giới thiệu 😀\tCV")!.SequenceEqual([13, 3]), "UTF-16 native range mapping failed");
            Check(WordRunBoundaries.TryRead(xml, "different") is null, "Mismatched native text accepted");
            Check(WordRunBoundaries.TryRead(prefix + "<w:r><w:drawing/></w:r>" + suffix, "") is null, "Object treated as editable plain text");
            Check(WordRunBoundaries.TryRead("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///bad'>]>" + xml, "Giới thiệu 😀\tCV") is null, "DTD accepted");
        });
        test("Word CV multiline patches use original indexes and preserve unrelated paragraphs", () =>
        {
            using var client = new OfficeHostClient(Path.Combine(AppContext.BaseDirectory, "H2AgentLab.OfficeHost.exe"), fixtureMode: true);
            var before = client.SnapshotWordAsync("word-fixture-1").Result;
            WordParagraphPatch[] patches = [new(0, "H2 Agent\r\nGiới thiệu\nKỹ năng", Bold: true), new(2, "Liên hệ\rThông tin")];
            var result = client.PatchWordAsync(new(before.SessionId, before.StateToken, true, patches)).Result;
            var after = client.SnapshotWordAsync(before.SessionId).Result;
            Check(OfficeMutationReadback.VerifyWordPatch(before, after, patches), "Multiline native protocol readback failed");
            Check(after.Paragraphs.Count == 6 && after.Paragraphs[3].Text == before.Paragraphs[1].Text, "Untargeted paragraph shifted incorrectly");
            Check(after.Paragraphs[4].Text == "Liên hệ", "Second original index shifted during first replacement");
            var corrupted = after.Paragraphs.ToArray(); corrupted[3] = corrupted[3] with { Text = "lost" };
            Check(!OfficeMutationReadback.VerifyWordPatch(before, after with { Paragraphs = corrupted }, patches), "Verifier missed lost outside text");
            corrupted = after.Paragraphs.ToArray(); corrupted[1] = corrupted[1] with { Runs = [new(0, "Giới thiệu", "Normal", false, false, false)] };
            Check(!OfficeMutationReadback.VerifyWordPatch(before, after with { Paragraphs = corrupted }, patches), "Verifier missed changed target formatting");
        });
        test("Word CV rejects invalid batches before any mutation and preserves stale-state guard", () =>
        {
            using var client = new OfficeHostClient(Path.Combine(AppContext.BaseDirectory, "H2AgentLab.OfficeHost.exe"), fixtureMode: true);
            var before = client.SnapshotWordAsync("word-fixture-1").Result;
            foreach (var patches in new WordParagraphPatch[][] {
                [new(0, "must not write"), new(50, "invalid")], [new(0, "first"), new(0, "duplicate")], [new(0, "unsafe\a")]
            })
            {
                var rejected = false;
                try { client.PatchWordAsync(new(before.SessionId, before.StateToken, true, patches)).GetAwaiter().GetResult(); }
                catch (OfficeHostClientException ex) when (ex.Code == "word_patch_rejected") { rejected = true; }
                Check(rejected && client.SnapshotWordAsync(before.SessionId).Result.StateToken == before.StateToken, "Rejected batch partially wrote text");
            }
            var stale = false;
            try { client.PatchWordAsync(new(before.SessionId, "old", true, [new(0, "wrong")])).GetAwaiter().GetResult(); }
            catch (OfficeHostClientException ex) when (ex.Code == "stale_state") { stale = true; }
            Check(stale, "Stale token accepted");
        });
        test("Word CV recovery requires verified same session state and original target set", () =>
        {
            using var client = new OfficeHostClient(Path.Combine(AppContext.BaseDirectory, "H2AgentLab.OfficeHost.exe"), fixtureMode: true);
            var before = client.SnapshotWordAsync("word-fixture-1").Result;
            var ledger = new OfficeRejectedMutationRecovery();
            WordParagraphPatch[] original = [new(0, "bad"), new(0, "duplicate")];
            var id = ledger.Reject("word.replace_range", before, original);
            WordParagraphPatch[] corrected = [new(0, "Giới thiệu\nKỹ năng")];
            Check(ledger.Resolve("word.replace_range", before, corrected, false).Length == 0, "Failed verification cleared error");
            Check(ledger.Resolve("word.replace_range", before with { SessionId = "other" }, corrected, true).Length == 0, "Other document cleared error");
            Check(ledger.Resolve("word.replace_range", before with { StateToken = "changed" }, corrected, true).Length == 0, "Changed state cleared error");
            Check(ledger.Resolve("word.replace_range", before, [new(1, "unrelated")], true).Length == 0, "Other paragraph cleared error");
            Check(ledger.Resolve("word.apply_format", before, corrected, true).Length == 0, "Formatting cleared rewrite error");
            Check(ledger.Resolve("word.replace_range", before, corrected, true).SequenceEqual([id]), "Corrected same-target failure remained unresolved");
            var formatId = ledger.Reject("word.replace_range", before, corrected);
            var formatted = before with { StateToken = "verified-format" };
            ledger.ObserveFormatting(before, formatted, [new(0, Bold: true)], false);
            Check(ledger.Resolve("word.replace_range", formatted, corrected, true).Length == 0, "Unverified format rebased rejection");
            ledger.ObserveFormatting(before, formatted, [new(1, Bold: true)], true);
            Check(ledger.Resolve("word.replace_range", formatted, corrected, true).Length == 0, "Unrelated format rebased rejection");
            ledger.ObserveFormatting(before, formatted, [new(0, Bold: true)], true);
            Check(ledger.Resolve("word.replace_range", formatted, corrected, true).SequenceEqual([formatId]), "Verified format then rewrite remained blocked");
        });
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
