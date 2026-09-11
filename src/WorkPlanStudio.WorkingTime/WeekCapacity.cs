namespace WorkPlanStudio.WorkingTime;

/// <summary>
/// What a week of a work center's calendar actually is. Three states, and they
/// are genuinely different:
/// <list type="bullet">
/// <item><see cref="Unconstrained"/> — the <c>continuous</c> pattern: an
/// unattended machine with no working-time constraint at all.</item>
/// <item><see cref="Staffed"/> — the ordinary case: one or more open windows.</item>
/// <item><see cref="Closed"/> — capacity zero: the rules removed every block, so
/// the plant may not run this centre at all.</item>
/// </list>
/// <para>
/// The reason this is a type and not a count is that the first and the last both
/// have an empty window list, and reading "no windows" as "no constraint" turns a
/// plant §9 forbids from running into a 24/7 machine — infinite longest
/// placement, a fully shaded Gantt, and not a word of warning. Making the caller
/// name which of the two it means is what removes that state from the model.
/// </para>
/// </summary>
public abstract record WeekCapacity
{
    private WeekCapacity()
    {
    }

    /// <summary>The open windows of the week; empty unless this is <see cref="Staffed"/>.</summary>
    public virtual IReadOnlyList<WeekWindow> Windows => [];

    /// <summary>True only for the continuous pattern — never for a pattern that was clipped away.</summary>
    public bool IsUnconstrained => this is Unconstrained;

    /// <summary>
    /// The capacity a set of windows describes: staffed when there is anything
    /// left, closed when the rules took it all. Never unconstrained — that comes
    /// from the pattern, not from a count.
    /// </summary>
    /// <param name="windows">What survived the rules.</param>
    /// <param name="causes">The rules that removed the rest, for the explanation.</param>
    public static WeekCapacity FromWindows(IReadOnlyList<WeekWindow> windows, IReadOnlyList<WorkingTimeRuleId> causes)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(causes);
        return windows.Count > 0 ? new Staffed(windows) : new Closed(causes);
    }

    /// <summary>An unattended machine: available around the clock, no crew whose working time is at stake.</summary>
    public sealed record Unconstrained : WeekCapacity;

    /// <summary>
    /// A staffed week. The window list is never empty — that is the point of the
    /// type — and equality is by value over it.
    /// </summary>
    public sealed record Staffed : WeekCapacity
    {
        /// <summary>Creates the staffed capacity.</summary>
        /// <param name="windows">At least one open window, sorted and disjoint.</param>
        public Staffed(IReadOnlyList<WeekWindow> windows)
        {
            ArgumentNullException.ThrowIfNull(windows);
            if (windows.Count == 0)
                throw new ArgumentException("A staffed week has at least one open window; use Closed for none.", nameof(windows));
            Windows = windows;
        }

        /// <inheritdoc />
        public override IReadOnlyList<WeekWindow> Windows { get; }

        /// <inheritdoc />
        public bool Equals(Staffed? other) => other is not null && Windows.SequenceEqual(other.Windows);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var window in Windows)
                hash.Add(window);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// A week with no working time left: the pattern was staffed, and the rules
    /// took all of it. A Sunday-only crew under §9, or a crew whose every block
    /// §5 pushed past its own end.
    /// </summary>
    public sealed record Closed : WeekCapacity
    {
        /// <summary>Creates the closed capacity.</summary>
        /// <param name="causes">The rules that emptied the week, in the order they fired.</param>
        public Closed(IReadOnlyList<WorkingTimeRuleId> causes)
        {
            ArgumentNullException.ThrowIfNull(causes);
            Causes = causes;
        }

        /// <summary>Which rules removed the working time, so a UI can say why the centre cannot run.</summary>
        public IReadOnlyList<WorkingTimeRuleId> Causes { get; }

        /// <inheritdoc />
        public bool Equals(Closed? other) => other is not null && Causes.SequenceEqual(other.Causes);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var cause in Causes)
                hash.Add(cause);
            return hash.ToHashCode();
        }
    }
}
