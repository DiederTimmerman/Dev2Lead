namespace Dev2Lead.Core;

public static class LearningCatalog
{
    public static readonly IReadOnlyList<LearningResource> All =
    [
        new("architecture", "Software architecture foundations", "Microsoft Learn", "https://learn.microsoft.com/en-us/azure/architecture/guide/", "Study guide"),
        new("well-architected", "Build well-architected systems", "Microsoft Learn", "https://learn.microsoft.com/en-us/azure/well-architected/", "Framework"),
        new("leadership", "Develop your leadership skills", "OpenLearn", "https://www.open.edu/openlearn/education-development/leadership-and-followership/content-section-overview", "Free course"),
        new("devops", "DevOps Engineer learning path", "Microsoft Learn", "https://learn.microsoft.com/en-us/credentials/certifications/devops-engineer/", "Certification path"),
        new("communication", "Effective communication in the workplace", "OpenLearn", "https://www.open.edu/openlearn/money-business/effective-communication-the-workplace/content-section-overview", "Free course"),
        new("csharp", "Build applications with C#", "Microsoft Learn", "https://learn.microsoft.com/en-us/training/browse/?terms=C%23", "Learning paths"),
        new("python", "Python programming foundations", "Python.org", "https://docs.python.org/3/tutorial/", "Study guide"),
        new("data", "Data science learning paths", "Microsoft Learn", "https://learn.microsoft.com/en-us/training/browse/?terms=data%20science", "Learning paths"),
        new("security", "Security learning paths", "Microsoft Learn", "https://learn.microsoft.com/en-us/training/browse/?terms=security", "Learning paths"),
        new("product", "Product management learning paths", "Microsoft Learn", "https://learn.microsoft.com/en-us/training/browse/?terms=product%20management", "Learning paths"),
        new("accessibility", "Web accessibility foundations", "W3C", "https://www.w3.org/WAI/fundamentals/", "Study guide"),
        new("agile", "The Scrum Guide", "Scrum.org", "https://www.scrumguides.org/scrum-guide.html", "Study guide"),
        new("wcag", "WCAG accessibility standards", "W3C", "https://www.w3.org/WAI/standards-guidelines/wcag/", "Standard"),
        new("nora", "NORA: Dutch government reference architecture", "NORA", "https://www.noraonline.nl/wiki/NORA_online", "Reference architecture"),
        new("udemy-python", "Explore Python courses", "Udemy", "https://www.udemy.com/courses/search/?q=python", "Course search"),
        new("udemy-leadership", "Explore technical leadership courses", "Udemy", "https://www.udemy.com/courses/search/?q=technical%20leadership", "Course search"),
        new("udemy-accessibility", "Explore WCAG and accessibility courses", "Udemy", "https://www.udemy.com/courses/search/?q=wcag", "Course search")
    ];

    public static IEnumerable<LearningResource> Filter(string provider, string query) => All.Where(resource =>
        (provider.Length == 0 || resource.Provider == provider)
        && (resource.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)
            || resource.Provider.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)));

    public static LearningResource Find(string id) =>
        All.FirstOrDefault(resource => resource.Id == id)
        ?? throw new InvalidDataException($"The coach returned an unknown learning resource: {id}.");

    public static CareerRoadmap Sample() => new()
    {
        TargetRole = "Lead Developer",
        Summary = "Turn strong individual contribution into technical leadership. Build evidence through architecture decisions, mentoring, and ownership of a team delivery.",
        EstimatedTime = "9-15 months",
        Assumptions = "Illustrative sample: a developer with 3+ years of experience, 5 hours/week for learning, and access to a team project. This is not an assessment of your CV or a guarantee of promotion.",
        FocusPoints =
        [
            new(1, "Think in systems, not tickets", "Lead developers connect technical choices to business outcomes.", "Write an architecture decision record for one real project and review it with your team.", "architecture"),
            new(2, "Multiply your team's potential", "Your impact grows when other developers grow.", "Mentor one colleague weekly and introduce constructive code-review guidelines.", "leadership"),
            new(3, "Own the delivery loop", "Reliable delivery builds trust and frees the team to innovate.", "Improve one CI/CD pipeline and track deployment frequency and failure rate.", "devops"),
            new(4, "Make your thinking visible", "Stakeholders need clarity, not just technical correctness.", "Present a short technical proposal with trade-offs to a non-technical stakeholder.", "communication"),
            new(5, "Lead a meaningful initiative", "Demonstrated leadership matters more than a job title.", "Own a scoped team initiative from planning through release and retrospective.", "agile")
        ],
        Milestones =
        [
            new("Months 1-3", "Build your foundation", "Create an architecture decision record, agree on a mentor, and establish a learning habit."),
            new("Months 4-6", "Lead in the small", "Mentor a teammate, improve delivery, and present a technical proposal."),
            new("Months 7-9", "Own the outcome", "Lead a cross-functional initiative and collect measurable evidence of impact."),
            new("Months 10-15", "Make the next move", "Review readiness with your manager and pursue a lead role based on demonstrated outcomes.")
        ]
    };
}
