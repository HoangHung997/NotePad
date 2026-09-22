using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using H2AgentLab.Verification;

namespace H2AgentLab.Tasking;

public enum AgentGoalInputOrigin { User, ModelProposal, RetrievedData }
public sealed record AgentGoalInput(Guid InputId, string Text, AgentGoalInputOrigin Origin = AgentGoalInputOrigin.User);
public enum AgentObligationStatus { Pending, AppliedUnverified, Verified, Failed, Superseded, WaivedByUser }

/// <summary>Immutable source-backed requirement. This is Agent contract state, not a second store.
/// Only the host's verifier report may attach verified evidence; model prose is never a verdict.</summary>
public sealed record AgentOutcomeObligation
{
    internal AgentOutcomeObligation(string id, string requirement, string sourceId, string revisionId,
        string scope, AgentObligationStatus status = AgentObligationStatus.Pending,
        IReadOnlyList<AgentEvidenceReference>? evidence = null, string? replacedBy = null)
    { Id=id; Requirement=requirement; SourceId=sourceId; RevisionId=revisionId; TargetScope=scope;
      Status=status; Evidence=Array.AsReadOnly((evidence ?? []).ToArray()); ReplacedBy=replacedBy; }
    public string Id { get; }
    public string Requirement { get; }
    public string SourceId { get; }
    public string RevisionId { get; }
    public string TargetScope { get; }
    public string VerificationStrategy => "host-criterion-verifier; no semantic proof from tool success";
    public AgentObligationStatus Status { get; }
    public IReadOnlyList<AgentEvidenceReference> Evidence { get; }
    public string? ReplacedBy { get; }
    public bool Active => Status is not (AgentObligationStatus.Superseded or AgentObligationStatus.WaivedByUser);
    internal AgentOutcomeObligation Change(AgentObligationStatus status,
        IReadOnlyList<AgentEvidenceReference>? evidence = null, string? replacedBy = null)
        => new(Id,Requirement,SourceId,RevisionId,TargetScope,status,(evidence ?? []).Concat(Evidence).Distinct().ToArray(),replacedBy ?? ReplacedBy);
}
public sealed record AgentGoalRevision(string Id, string? ParentId, int Sequence, string SourceId,
    string SourceText, IReadOnlyList<string> Added, IReadOnlyList<string> Retired);

/// <summary>Bounded immutable work state inside AgentTaskContract. No persistence/resume engine.
/// Deterministic extraction preserves explicit clauses, not a claim of unrestricted NLP.
/// Ambiguous edits retain old obligations and add the new instruction, never silently weaken it.</summary>
public sealed class AgentGoalState
{
    public const int MaxRevisions = 25;
    public const int MaxObligations = 64;
    private static readonly Regex WorkVerb = new(@"\b(sửa|tạo|xuất|ghi|xóa|thay|edit|create|export|write|save|update|replace|delete|modify|generate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Replacement = new("^(?:replace|thay(?: thế)?)\\s+[\"“](?<old>[^\"”]+)[\"”]\\s+(?:with|bằng|thành)\\s+[\"“](?<new>[^\"”]+)[\"”]\\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Cancellation = new("^(?:cancel|hủy|bỏ)\\s+(?:(?:outcome|mục)\\s+(?<index>[0-9]+)|[\"“](?<text>[^\"”]+)[\"”])\\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private AgentGoalState(Guid taskId, string scope, IEnumerable<AgentGoalRevision> revisions,
        IEnumerable<AgentOutcomeObligation> obligations, IEnumerable<string>? mutationRevisions = null)
    { TaskId=taskId; Scope=scope; Revisions=Array.AsReadOnly(revisions.ToArray());
      Obligations=Array.AsReadOnly(obligations.ToArray()); MutationRevisions=Array.AsReadOnly((mutationRevisions ?? []).Distinct().ToArray()); }
    public Guid TaskId { get; }
    public string Scope { get; }
    public IReadOnlyList<AgentGoalRevision> Revisions { get; }
    public IReadOnlyList<AgentOutcomeObligation> Obligations { get; }
    public IReadOnlyList<string> MutationRevisions { get; }
    public string RevisionId => Revisions[^1].Id;
    public IReadOnlyList<AgentOutcomeObligation> Active => Array.AsReadOnly(Obligations.Where(x=>x.Active).ToArray());
    public bool HasOutcomes => Obligations.Count != 0;

    public static AgentGoalState Create(Guid taskId, string scope, AgentGoalInput input)
    {
        if(taskId==Guid.Empty)throw new ArgumentException("Task identity is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return new AgentGoalState(taskId,scope,[],[]).Apply(input);
    }
    public AgentGoalState Apply(AgentGoalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if(input.Origin!=AgentGoalInputOrigin.User)
            throw new InvalidOperationException("Only an input from the host user boundary can revise or waive requirements.");
        if(input.InputId==Guid.Empty || string.IsNullOrWhiteSpace(input.Text) || input.Text.Length>8_000)
            throw new ArgumentException("A bounded user message and stable input ID are required.");
        var source="user:"+input.InputId.ToString("N");
        var replay=Revisions.FirstOrDefault(x=>x.SourceId==source);
        if(replay is not null)
        {
            if(replay.SourceText!=input.Text)throw new InvalidOperationException("Input ID reused with different content.");
            return this;
        }
        if(Revisions.Count>=MaxRevisions)throw new InvalidOperationException("Goal revision limit reached; no source was discarded.");
        var revision=Id("revision",TaskId.ToString("N"),source);
        var next=Obligations.ToList();var added=new List<string>();var retired=new List<string>();
        void Add(string requirement)
        {
            if(next.Count>=MaxObligations)throw new InvalidOperationException("Outcome limit reached; no requirements were discarded.");
            var id=Id("goal",TaskId.ToString("N"),source+":"+added.Count);
            next.Add(new(id,requirement,source,revision,Scope));added.Add(id);
        }
        var text=input.Text.Trim();var replace=Replacement.Match(text);var cancel=Cancellation.Match(text);
        var handled=false;
        if(Revisions.Count>0 && replace.Success)
        {
            var matches=Active.Where(x=>x.Requirement==replace.Groups["old"].Value).ToArray();
            if(matches.Length==1)
            {
                var old=matches[0];Add(replace.Groups["new"].Value);
                next[next.FindIndex(x=>x.Id==old.Id)]=old.Change(AgentObligationStatus.Superseded,replacedBy:added[0]);
                retired.Add(old.Id);handled=true;
            }
        }
        if(Revisions.Count>0 && cancel.Success)
        {
            var candidates=Active.ToArray();AgentOutcomeObligation? selected=null;
            if(int.TryParse(cancel.Groups["index"].Value,out var index) && index>=1 && index<=candidates.Length)
                selected=candidates[index-1];
            else if(cancel.Groups["text"].Success)
            {var matches=candidates.Where(x=>x.Requirement==cancel.Groups["text"].Value).ToArray();if(matches.Length==1)selected=matches[0];}
            // A waiver is prospective. Completed/applied work is not undone or erased.
            if(selected is {Status:AgentObligationStatus.Pending or AgentObligationStatus.Failed})
            {next[next.FindIndex(x=>x.Id==selected.Id)]=selected.Change(AgentObligationStatus.WaivedByUser);retired.Add(selected.Id);handled=true;}
        }
        if(!handled)
        {
            var clauses=Clauses(text);
            // Simple conversation retains source/revision only, without a mandatory checklist.
            // Multi-outcome work is conservative: retain every clause, including preservation/export.
            if(HasOutcomes || clauses.Length>1 && WorkVerb.IsMatch(text))
                foreach(var clause in clauses)Add(clause);
        }
        if(next.Where(x=>x.Active).Sum(x=>x.Requirement.Length)>8_000)
            throw new InvalidOperationException("Active requirements exceed their bound; no source was truncated.");
        var item=new AgentGoalRevision(revision,Revisions.LastOrDefault()?.Id,Revisions.Count+1,source,input.Text,
            Array.AsReadOnly(added.ToArray()),Array.AsReadOnly(retired.ToArray()));
        return new(TaskId,Scope,Revisions.Append(item),next,MutationRevisions);
    }

    /// <summary>Model proposals may only ADD exact quotations from existing user sources.
    /// A proposal carries neither a waiver nor evidence; existing requirements always remain.</summary>
    public AgentGoalState AddProposal(string sourceId, string exactRequirement)
    {
        var source=Revisions.SingleOrDefault(x=>x.SourceId==sourceId)
            ?? throw new InvalidOperationException("Proposal has no user source in this task.");
        if(string.IsNullOrWhiteSpace(exactRequirement) || !source.SourceText.Contains(exactRequirement,StringComparison.Ordinal))
            throw new InvalidOperationException("Proposed requirement is not an exact user-source quotation.");
        if(Active.Any(x=>x.Requirement==exactRequirement))return this;
        if(Obligations.Count>=MaxObligations || Active.Sum(x=>x.Requirement.Length)+exactRequirement.Length>8_000)
            throw new InvalidOperationException("Proposal exceeds the bounded contract.");
        return new(TaskId,Scope,Revisions,Obligations.Append(new AgentOutcomeObligation(
            Id("goal",TaskId.ToString("N"),sourceId+":"+exactRequirement),exactRequirement,sourceId,RevisionId,Scope)),MutationRevisions);
    }
    public AgentGoalState RecordMutation(string dispatchedRevision)
    {
        if(!Revisions.Any(x=>x.Id==dispatchedRevision))throw new InvalidOperationException("Unknown dispatch revision.");
        return new(TaskId,Scope,Revisions,Obligations,MutationRevisions.Append(dispatchedRevision));
    }
    public AgentGoalState Observe(string dispatchedRevision, VerificationReport report,
        IReadOnlyList<AgentEvidenceReference> observedEvidence)
    {
        ArgumentNullException.ThrowIfNull(report);ArgumentNullException.ThrowIfNull(observedEvidence);
        if(dispatchedRevision!=RevisionId)throw new InvalidOperationException("Stale verification cannot satisfy a revised goal.");
        var items=Obligations.ToArray();
        foreach(var result in report.Criteria)
        {
            var index=Array.FindIndex(items,x=>x.Id==result.CriterionId && x.Active);
            if(index<0)continue; // generic tool success never claims semantic coverage of all goals
            var references=observedEvidence.Where(e=>result.EvidenceIds.Contains(e.ReferenceId,StringComparer.Ordinal)).ToArray();
            var status=result.Status==VerificationCriterionStatus.Failed?AgentObligationStatus.Failed
                : result.Status==VerificationCriterionStatus.Passed && references.Length>0
                    ? AgentObligationStatus.Verified : AgentObligationStatus.AppliedUnverified;
            items[index]=items[index].Change(status,references);
        }
        return new(TaskId,Scope,Revisions,items,MutationRevisions);
    }
    public void EnsureComplete()
    {
        var missing=Active.Where(x=>x.Status!=AgentObligationStatus.Verified).Select(x=>x.Id+"="+x.Status).ToArray();
        if(missing.Length>0)throw new InvalidOperationException("Unfulfilled user outcomes: "+string.Join("; ",missing));
    }
    public string Describe()
        => !HasOutcomes ? "" : "[HOST OUTCOMES revision="+RevisionId+"]\n"
            +string.Join("\n",Active.Select(x=>x.Id+" ["+x.Status+"] "+x.Requirement))
            +"\nOnly host criterion verification with source evidence satisfies these outcomes. Tool success or final text does not. User revisions do not grant permissions.";
    private static string[] Clauses(string text)
    {
        // Quoted payloads/formulas remain intact. No semantic rewrite of numbers/paths.
        var parts=new List<string>();var start=0;char quote='\0';
        for(var i=0;i<text.Length;i++)
        {
            if(quote!='\0'){if(text[i]==quote)quote='\0';continue;}
            if(text[i] is '"' or '“'){quote=text[i]=='“'?'”':'"';continue;}
            var split=text[i]=='\n' || text[i]==';' && i+1<text.Length && char.IsWhiteSpace(text[i+1])
                || i>0 && i+1<text.Length && text[i]=='+' && text[i-1]==' ' && text[i+1]==' ';
            if(!split)continue;
            var part=text[start..i].Trim();if(part.Length>0)parts.Add(part);start=i+1;
        }
        var last=text[start..].Trim();if(last.Length>0)parts.Add(last);
        if(parts.Count<2 || parts.Any(x=>x.Length<4))return [text];
        return parts.Select(x=>Regex.Replace(x,@"^(?:[-*•]\s+|[0-9]+[.)]\s+)","",RegexOptions.CultureInvariant)).ToArray();
    }
    private static string Id(string kind,string task,string value)
        => kind+":"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(task+":"+value))).ToLowerInvariant()[..24];
}
