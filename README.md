# Dev2Lead

A Windows .NET MAUI Blazor Hybrid career coach, built for a one-hour vibe-coding demo.

The .NET solution, projects, namespaces, application identity and scripts use Dev2Lead.
The existing Cosmos database `Northstar`, Blob container `northstar-cvs`, and User Secrets IDs
remain unchanged to preserve stored data and local configuration.

## Run

Prerequisites: .NET 10 SDK, `maui-windows` workload, Windows WebView2, Azure CLI for
backend development authentication, and a Google **Desktop app** OAuth client.

Start the backend in one terminal, then the desktop app in another:

```powershell
.\Start-Dev2Lead-Api.ps1
```

```powershell
.\Start-Dev2Lead.ps1
```

Or run the project directly:

```powershell
dotnet build .\src\Dev2Lead\Dev2Lead.csproj -f net10.0-windows10.0.19041.0
dotnet run --project .\src\Dev2Lead\Dev2Lead.csproj -f net10.0-windows10.0.19041.0
```

`Dev2Lead.slnx` contains the Windows app, shared core, ASP.NET Core backend, and xUnit tests.
The local API listens at `http://127.0.0.1:5246`; `/health` checks the running process,
not storage availability. Development OpenAPI is at `/openapi/v1.json`. Authenticated
request examples are in `src\Dev2Lead.Api\Dev2Lead.Api.http`.

**Permissions**, available only in the header, contains four account consent choices:
CV storage, AI CV analysis, career coaching and LinkedIn import. These are stored in
Cosmos and checked server-side on each new operation. All choices default to off.
The first successful signed-in settings and profile reads create default account and empty
profile records when absent; an empty profile is still treated as no uploaded CV.
Revocation prevents future processing but does not undo completed work or erase saved data.
The dialog opens immediately, including when signed out, and offers Google sign-in when needed.
Saved permissions must load before editing/saving is enabled. Loading errors stay visible inside
the dialog with a retry action; cancelling an in-flight load cancels the request.
If Cosmos cannot be reached, the API returns a storage-specific 503 error rather than an AI error.
Check `Cosmos:Endpoint` in the API's User Secrets, DNS/network access and the actual account URI
in Azure. The dialog never enables saving default values after a failed load.
There are no API key, endpoint, Google credential or backend connection fields in the UI.
Developer connection configuration belongs exclusively in User Secrets/environment variables.
Use HTTPS for remote backends; HTTP is allowed only on loopback for development.

### Aspire

Alternatively, launch the .NET 10 / Aspire 13.6.1 AppHost:

```powershell
.\Start-Dev2LeadAspire.ps1
```

It starts the API on the fixed local callback port, provides the Aspire dashboard,
health monitoring and OpenTelemetry logs/traces/metrics, and lists `dev2lead-desktop`
as an **explicit-start** executable. Start that resource from the dashboard when ready.
The desktop receives its backend URL from the AppHost environment. Google development
credentials still come from the desktop's own User Secrets. Do not also run the standalone
API script on the same port. No Docker/emulator is needed: cloud accounts remain external.
Aspire is local orchestration here, not a cloud deployment of the MAUI desktop.
The development `http` profile explicitly allows loopback-only HTTP transport.
Use the `https` profile for a TLS dashboard; deployed backends must use HTTPS.
OAuth callbacks are excluded from request traces to avoid recording authorization codes.

Azure AI settings now belong to the **backend**, not the desktop app. Use a chat deployment
supporting JSON mode, such as GPT-4.1 or GPT-5 nano. For `services.ai.azure.com` endpoints,
including `/api/projects/PROJECT` URLs, the backend calls
the resource's `/openai/v1/chat/completions` endpoint with the deployment in the `model` field.
The project path is not a model-inference endpoint. Existing `openai.azure.com` resource
URLs keep using the deployment-based API (`2024-10-21`). Generic OpenAI endpoints are not supported.

### Local development secrets

The desktop Debug build loads these .NET User Secrets:

- `Google:ClientId`
- `Google:ClientSecret` (the installed-client value in Google's downloaded JSON)
- `Backend:Url`

The desktop does not require or read `AzureOpenAI:*` settings. Any legacy copies of
the endpoint, deployment and API key can be removed from its User Secrets; keep them
only in the API project's configuration.

The backend has a separate User Secrets store, with:

- `Google:ClientId` (must match the desktop OAuth client)
- `AzureOpenAI:Endpoint`
- `AzureOpenAI:Deployment`
- `AzureOpenAI:ApiKey`
- `Cosmos:Endpoint`
- `Cosmos:Key` (the supplied account key is stored only in backend User Secrets)
- `Storage:ConnectionString` (Blob Storage connection string, backend only)
- `Azure:TenantId` (also configurable in backend appsettings)
- `LinkedIn:ClientId`
- `LinkedIn:ClientSecret`
- `LinkedIn:RedirectUri`

Use `dotnet user-secrets set --project .\src\Dev2Lead.Api\Dev2Lead.Api.csproj` with
JSON piped through standard input, or Visual Studio's **Manage User Secrets** for this project.
Use the desktop project path for its separate store. Do not copy downloaded OAuth files or
real keys into the workspace. User Secrets are outside the workspace, but **not encrypted**
and are for local development only. Release builds do not load them. An installed application's
Google client secret is not a confidential server credential. Desktop Google configuration
comes only from User Secrets/environment variables; only its refresh token is persisted in
SecureStorage. Azure AI, LinkedIn confidential-client and storage credentials are never sent
to the desktop. Legacy connection preferences are no longer read.
Backend production configuration can use environment variables such as `Google__ClientId`
and `AzureOpenAI__ApiKey`, with proper secret management. Rotate keys exposed in chats or logs.

### Google sign-in

Use Google's **Desktop app** OAuth client, not a Web application client. The supplied
downloaded client configuration has been imported into local desktop User Secrets; only
its client ID is copied into backend configuration. Configure the OAuth consent screen and,
when the application is in testing, include permitted Google accounts as test users.

Sign-in opens the system browser with PKCE, random state and nonce, and an ephemeral
loopback callback. The desktop validates Google's signed ID token, client audience and nonce.
Only the refresh token is persisted in MAUI SecureStorage; the ID token remains in memory.
The backend independently validates Google's signature, issuer, audience and token lifetime.
Profile partition keys come from the authenticated Google subject, never email or a supplied
user ID. Sign out removes the local token and clears loaded CV, skills, experience, chat,
roadmap and consent. It does not sign out the system browser or revoke sessions on other devices.

### Azure storage setup

Dedicated resources:

- Cosmos account `cosmos-agents12`: database `Northstar`, container `CareerProfiles`,
  partition key `/userId`. One versioned document per Google account contains CV text,
  work experience, skill estimates, review status and upload metadata.
- Storage account `saaiagentsworkflow`: **private** Blob container `northstar-cvs`
  stores original PDF/DOCX/TXT bytes under generated names. Originals are too large
  for Cosmos's 2 MB document limit; no public Blob URLs are returned.

The private Blob container was created. The supplied Cosmos key is configured in backend
User Secrets, avoiding the earlier data-plane RBAC dependency for Cosmos requests.
It does **not** create the database/container or bypass management-plane MFA policies.
Live infrastructure deployment and account persistence are not claimed by local tests.

Complete tenant-specific MFA in your own terminal:

```powershell
az login --tenant 0071de0e-b827-493b-a8d9-6db970b9cd15 --scope "https://management.core.windows.net//.default"
.\Setup-Dev2Lead-Azure.ps1 -GrantDevelopmentIdentityAccess
```

If Azure CLI supplies a claims-challenge login command, use that exact challenge to satisfy
the tenant's policy. The setup script now applies the subscription-scoped `infra\main.bicep`
and its checked-in, nonsecret parameters to the existing accounts in their resource groups.
Re-running applies declared changes, not just one-time create-if-missing logic.
It creates the dedicated Cosmos database/container and private Blob container, declares
`/userId`, and optionally grants the signed-in development principal **Cosmos DB
Built-in Data Contributor** at `/dbs/Northstar/colls/CareerProfiles`. Blob access uses the
backend-only `Storage:ConnectionString` secret and needs no Blob data-role assignment. An identity
with permission to create the Cosmos assignment must run it. It does not delete
or replace existing accounts and does not create provisioned throughput on this serverless account.
The container has no user-field indexes because the application uses point reads for `profile`
and `account` documents. Bicep outputs endpoints/names only, never credentials.

If the Cosmos account itself is missing, explicitly enable account provisioning:

```powershell
.\Setup-Dev2Lead-Azure.ps1 -CreateCosmosAccount -CosmosOnly -GrantDevelopmentIdentityAccess -ValidateOnly
.\Setup-Dev2Lead-Azure.ps1 -CreateCosmosAccount -CosmosOnly -GrantDevelopmentIdentityAccess -WhatIf
.\Setup-Dev2Lead-Azure.ps1 -CreateCosmosAccount -CosmosOnly -GrantDevelopmentIdentityAccess
```

This mode manages the serverless `cosmos-agents12` account in `westeurope` through a pinned
Azure Verified Module, then creates `Northstar` / `CareerProfiles`. `-CosmosOnly` leaves Blob
resources untouched. Existing-account mode remains the default; do not enable account management
for an unrelated account with different settings. No resources or existing documents are deleted.
Recreating a deleted account does not restore its former documents.
The current recreation attempt compiled successfully, but ARM validation and what-if were
blocked by required interactive Azure MFA (`AADSTS50076`). No account was deployed.
Complete the CLI's prompted MFA login/claims challenge, then rerun validation and preview.
`infra\cosmos-recreate.parameters.json` records the approved Cosmos-only account and development
principal; update the principal when deploying for another identity.

Newly provisioned accounts use Entra authentication only. Remove an old `Cosmos:Key` from the API's
User Secrets and use the assigned development identity; a key from a deleted account is invalid.
The desktop still receives no database credentials. Keep `-CreateCosmosAccount` on subsequent
deployments if Bicep is managing the account.

Preview declarative changes with `.\Setup-Dev2Lead-Azure.ps1 -WhatIf`. For a production
managed identity with Cosmos data access, use `-BackendPrincipalId <object-id>`.
These are incremental deployments: removing a declaration does not automatically delete
previously deployed resources. Use a reviewed migration for incompatible partition-key changes.

Cosmos uses `AzureCliCredential` in Development and `DefaultAzureCredential` in production
unless `Cosmos:Key` is configured. Blob Storage always uses the backend-only
`Storage:ConnectionString`; store it in User Secrets locally and an appropriate secret store
in production. Neither credential is exposed to the desktop. Configure a trusted HTTPS host
before attempting cross-device use: the current loopback server is local only.

## Two-minute demo

1. Start the configured backend, then **Sign in with Google** to load your private profile.
2. Save CV storage and AI extraction permission in **Permissions**, then use the **one**
   CV upload panel on Overview to upload a PDF,
   DOCX, or TXT CV. `samples\Demo-CV.txt` is fictional data for a safe demo. The original
   and readable text are stored first; AI then extracts and immediately saves experience
   and tentative skills. A failed extraction retains the saved CV and offers retry.
   There is no sample-preview action beside CV upload.
3. Enable coaching permission and tell the coach: “I want to become a lead developer.”
4. Answer its questions about experience, weekly learning time, and your deadline.
5. Overview presents a narrative about your plan, with **Open my roadmap** linking to
   the dedicated roadmap page. Click **Build my personal roadmap** for five priorities, curated study links,
   and a milestone timeline with explicit assumptions.
6. Mark a first action as done and **Export my roadmap** to a local Markdown file.
   Exports are saved in the Windows application-data directory; the app displays the path.

## Microsoft Learn achievements

The **Microsoft Learn** tab syncs a public transcript sharing URL and the associated public
achievement collection. It shows active and historical certifications, searchable completed
modules, and earned module badges, learning-path trophies, and course awards with Microsoft's
artwork. Completion dates, expiry dates, training time, and links back to Learn are included.
Module and badge lists initially show 24 entries; **Show more** reveals the rest.
Some completed resources no longer have a URL in Microsoft's response. Those are labeled
**Resource link unavailable** and link to the transcript instead; Dev2Lead does not invent URLs.

Sign in with Google, paste your sharing link into the tab and click **Save & sync transcript**.
The URL is stored in the separate `account` Cosmos document in your Google-subject partition,
even when you have no CV. It is no longer read from User Secrets or device preferences.
The old development transcript secret has been removed; enter your link once for your account.
Achievement data stays in memory. Reopening the tab loads your account's URL and refreshes it.
The sharing URL grants access to your public transcript; treat it accordingly.
No legal name, contact email, certification numbers, or Azure credentials are used for this sync,
and the fetched achievements are not sent to the AI coach.

The integration uses the same public profile endpoints as the Microsoft Learn website,
not a guaranteed stable API. Network/schema failures are shown explicitly; a failed refresh
labels any previous successful data as stale rather than reporting success.

## Skills profile and learning sources

**My skills** separates Technical, Soft skills, and Business & standards (including WCAG and
NORA). CV upload initializes the saved profile automatically when AI analysis is enabled
in saved Permissions; otherwise it stores the CV without sending it to AI. To reassess,
enable that permission and click **Suggest skills from CV**. Skills links back to the
single Overview CV panel rather than providing another upload control.
The AI returns tentative proficiency on a five-point scale: Novice, Advanced beginner,
Competent, Proficient, Expert. Each skill also has total non-overlapping years of experience;
insufficient evidence is **Unknown**, not zero. Any estimated value requires a verbatim CV excerpt.
WCAG and NORA are included as unknown when not evidenced; familiarity is not assumed.

Edit names, categories, levels, years, and notes. Confirm each assessment and **Save reviewed
profile** to save edits to Cosmos under your signed-in Google account. Initial AI values
are saved as **unreviewed** immediately; confirmation is required only for manual saves.
Generating again requires permission to replace the **saved** skills and work experience,
including unsaved edits. CV replacement also resets the saved skills and experience.
ETags prevent stale saves from overwriting another device's updates; refresh and reapply
edits when there is a conflict. Header **Refresh profile** replaces unsaved on-screen edits.
The old device-global `skills-profile.json` is no longer read or written by the app and is not
silently migrated. Legacy files remain on disk until the user removes them.

**My experience** displays evidenced job titles, employers, date ranges and responsibilities.
Unknown dates remain unknown. Skills are not inferred from Learn certifications and manual
edits are not automatically added to the coaching chat.

**Learning library** now filters by source (Microsoft Learn, Python.org, OpenLearn, W3C,
NORA, Scrum.org, or Udemy) and title/source search. Udemy entries are explicitly labeled
**Course search** links for Python, technical leadership, and WCAG. They are not scraped
course listings or endorsements, and do not claim current prices, ratings, or availability.
Review the course syllabus and instructor before enrolling. Permission settings are available
only in the header, not the left navigation.

## Designer branded visual

The overview hero features a CSS 3D logo panel surrounded by connected geometric nodes,
orbital rings, and a perspective grid. **Pause motion** freezes the animation; Windows
reduced-motion preferences automatically disable it.

The supplied Designer PNG is copied unchanged into the app's packaged assets. It replaces
the BBTG image/link, works offline and has an explicit unavailable state if loading fails.
The header's signed-out Google button uses Google's published G artwork. When signed in,
the account name/initials are in the header, not the lower sidebar; click to see email,
refresh the profile or sign out.

## LinkedIn import

Enable LinkedIn import in Permissions, then use **Import LinkedIn profile** on My experience.
Client credentials are stored only in backend User Secrets. Register this exact local callback
in your LinkedIn developer app:

```text
http://localhost:5246/api/linkedin/callback
```

Enable LinkedIn's **Sign In with LinkedIn using OpenID Connect** product with `openid profile email`.
Credentials alone are insufficient; complete browser authorization. The backend validates
the signed ID token against discovery keys, issuer, audience, expiry and nonce, then calls
`https://api.linkedin.com/v2/userinfo` and checks its subject against the token.
Short-lived, hashed state is bound to the Google account, expires after ten minutes and is
consumed before exchange. No LinkedIn access/refresh token is retained.

Refresh the account from the header after browser completion. Imported name, email,
verified-email flag and picture URL are stored in your Cosmos account document. This endpoint
does **not** return employment history, skills, full profile text or a CV; those still come
from CV extraction. Imported identity does not replace Google authentication or certify a
person's real-world identity. **Remove imported profile** erases LinkedIn fields while retaining
CV, transcript and preferences. For remote use, configure/register a matching HTTPS callback.

No 3D engine dependency is needed: the MAUI Blazor WebView supports CSS 3D transforms.
`Toolkit.Maui.Shapes3D` is an unofficial native MAUI library, not an official
`CommunityToolkit.Maui.Shapes3D` package; it is not used in this implementation.

## Privacy and scope

- With saved upload consent, original CV bytes persist in private Blob Storage and extracted
  text, experience and skill assessments persist in Cosmos. The profile is scoped to the
  validated Google subject. Every profile, AI and original-download endpoint requires authentication.
- Experience/skills extraction and chat run through the backend's Azure AI deployment.
  Chat requires its own saved consent. Only the latest 24 messages are sent; chat and roadmaps
  are not persisted in Cosmos. Azure service retention policies apply.
- Sign-out clears account data from memory but retains cloud data. Confirm **Delete stored
  CV and profile** to delete cloud metadata and originals. Deletion first stores a durable
  `Deleting` state, so a storage failure can be refreshed and retried without losing the
  reference to the original. Original download and coaching are blocked during deletion.
- Transcript URL, saved permissions and imported LinkedIn identity are independent account
  metadata, so deleting a CV does not erase them. Sign-out clears loaded account data;
  another Google account never inherits global transcript or consent values.
- Replacing a CV clears the conversation and roadmap. Obsolete-original references remain
  in metadata until cleanup succeeds, so a failed replacement cleanup can still be deleted.
  Blob and Cosmos writes are not a distributed transaction. If upload metadata and its
  compensating Blob deletion both fail, an operator must clean the orphaned generated Blob.
  Azure soft-delete, versioning and backups may retain recoverable data per account policy.
- File extraction is checked both locally and server-side; scanned PDFs need a text-based
  export. Maximum: 10 MB, 30 PDF pages, and 40,000 extracted characters.
- Learning URLs come from a curated catalog, not live web search. Provider availability,
  pricing, and prerequisites can change. Career estimates are guidance, not promises.
- The backend includes bounded uploads, per-account rate limiting, ETag concurrency and
  safe Problem Details. No anonymous or offline-success fallback is used if Azure fails.
- Local tests use explicitly injected storage/AI test doubles. They do not prove live
  Google browser consent, Cosmos/Blob connectivity or successful cloud deployment.
- Before distribution, deploy the backend over HTTPS with managed identity, monitored
  retention/cleanup policies and production secret management. Settings alone do not
  prove that the backend's Azure connection works.

## Growth coaches and Career Gap Analysis

The sidebar now includes **Career Gap Analysis**, **Technical Lead Coach**, **Engineering Manager
Coach** and **Interview Coach**. These are distinct, role-focused in-app AI agents using the existing
backend model, not separately provisioned Foundry agent resources. No infrastructure change is needed.

- Technical Lead Coach: architecture trade-offs, coding and constructive reviews.
- Engineering Manager Coach: leadership, feedback, delegation and people management.
- Interview Coach: select Promotion preparation, Behavioral questions or Intake training, then send
  the prepared prompt or adapt it. The coach asks one practice question at a time and gives feedback.

All growth endpoints require Google authentication, saved Career coaching permission and the current
profile version. The backend reads only the signed-in account's saved CV and skills. Unsaved skill
edits are not used. Guidance and AI target levels remain provisional and never guarantee promotion.
Coach conversations and gap results remain in memory during navigation, reset when the saved profile
changes or on sign-out, and do not survive an app restart.

Gap Analysis suggests 3-30 role-specific target competencies on the Novice-to-Expert scale. The app,
not the model, computes `100 * sum(max(target - current, 0)) / sum(target)` for known current levels.
0 means the smallest evidenced gap, 100 the largest; unknown levels are excluded and displayed with
coverage. If all levels are unknown, no numeric score is shown. Unreviewed AI levels are explicitly
marked provisional. The competence matrix shows current/target levels, gaps, evidence and practice
actions. This is advisory self-development guidance, not a certified readiness or hiring score.

**My skills** now opens as summary cards with level, years, evidence and input-required warnings.
Click **Edit** for the assessment form, confirm it, then save the reviewed profile. Unknown values
remain allowed when evidence is absent. An expandable competence matrix compares all current skills.
Roadmaps still contain five focus points; the timeline requires at least three complete milestones
and no longer has a five-milestone maximum.

## Verification

```powershell
dotnet test .\src\Dev2Lead.Core.Tests\Dev2Lead.Core.Tests.csproj
dotnet test .\src\Dev2Lead.Api.Tests\Dev2Lead.Api.Tests.csproj
```

Core tests cover CV extraction/limits, AI evidence and date validation, provisional skill
initialization, Azure request shape, and the exactly-five-priorities roadmap contract.
API tests cover authentication rejection, Google-subject isolation despite identical emails,
private original access, consent, immediate persistence, failure/retry, reviewed edits,
version conflicts, durable deletion and replacement cleanup. Account/LinkedIn tests cover
default-deny consent, revocation, transcript ownership, expiring/single-use OAuth state,
account binding, cancellation and removal of imported identity.

Native interface checks cover the signed-out Google control, a single CV upload, the narrative and
designer image, experience/skills pages, Udemy filters, header-only permissions and page layout.
The account/Aspire redesign passed 41 API tests, 39 focused Core tests and 16 native interface checks.
The growth-coaching update passed 30 targeted API tests, 62 focused Core tests and 28 native checks,
including skill edit/summary transitions, interview choices, draft isolation and navigation warnings.
The desktop build was warning-free. Earlier AppHost builds were also warning-free, and the Aspire dashboard and managed API health responded,
and anonymous account settings were rejected. Bicep templates compile but have not been deployed.
The signed-in account menu, live account preference saves, LinkedIn browser import and end-to-end
CV storage/extraction still require authenticated cloud/provider checks. Azure provisioning and Blob
permissions remain deferred pending tenant MFA/data-plane permissions.
