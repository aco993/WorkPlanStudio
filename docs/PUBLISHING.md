# Publishing to GitHub

A concrete, step-by-step guide to put this repository on GitHub and turn on the
live demo. It assumes you have **git** and a **GitHub account**; the
[GitHub CLI (`gh`)](https://cli.github.com) is optional but makes step 3 a one-liner.

## 1. First commit

From the repository root (`WorkPlanStudio/`):

```bash
git init -b main
git add .
git commit -m "Initial commit: WorkPlan Studio"
```

The repo already ships a `.gitignore` (build output, `artifacts/`, `.claude/`) and a
`.gitattributes` (line-ending normalisation), so nothing unwanted is committed.

## 2. Replace the placeholders

Two files reference a `your-username` placeholder — replace it with your GitHub
username. Search for it first:

```bash
git grep -n "your-username"
```

You'll find it in **`README.md`** and **`README.de.md`**:

- the **CI badge** URL — `https://github.com/your-username/WorkPlanStudio/...`
- the **live-demo** URL — `https://<your-username>.github.io/WorkPlanStudio/`

Fix them, then:

```bash
git commit -am "Set GitHub username in README"
```

## 3. Create the repository and push

**With the GitHub CLI:**

```bash
gh repo create WorkPlanStudio --public --source=. --remote=origin --push
```

**Or via the website:** create a new empty repo named `WorkPlanStudio` (no README),
then:

```bash
git remote add origin https://github.com/<your-username>/WorkPlanStudio.git
git push -u origin main
```

## 4. Enable the live demo (GitHub Pages)

1. On GitHub, open **Settings → Pages**.
2. Set **Source = GitHub Actions**.
3. The `deploy.yml` workflow runs on every push to `main`; once it finishes, the app
   is live at `https://<your-username>.github.io/WorkPlanStudio/`.

The deploy is **gated on the engine tests** — if they fail, it won't publish.

## 5. Check the workflows

Open the **Actions** tab. On the first push you should see:

- **CI** — engine tests + coverage, and the mapper/component tests.
- **E2E** — Playwright tests (downloads a browser; takes a few minutes).
- **Deploy to GitHub Pages** — build + publish (only on `main`).

Green across the board means the badges in the README will go green too.

## Troubleshooting

- **Pages 404 / blank page** — make sure Pages Source is *GitHub Actions* (not
  "Deploy from a branch"), and that the deploy workflow finished.
- **Default branch isn't `main`** — `deploy.yml` triggers on `main`. Either push to
  `main` or change the branch in the workflow.
- **CI badge shows "no status"** — it only resolves after the first workflow run on
  GitHub; it will fill in once Actions has run.
