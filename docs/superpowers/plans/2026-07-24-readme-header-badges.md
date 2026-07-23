# README Header Badges Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the approved centered project header and exactly three compact two-part badges to the GitHub README.

**Architecture:** Use README-compatible HTML for centered layout and Shields.io badge images for the requested metadata. Modify only the README header; keep every existing section unchanged.

**Tech Stack:** GitHub Markdown, HTML, Shields.io

## Global Constraints

- Show exactly `platform | Windows x64`, `built with | .NET 9 · WPF`, and `helper | codex-auth 0.2.10`.
- Do not add version, downloads, license, build, test, or other badges.
- Keep all existing README sections unchanged.

---

### Task 1: Add the centered README header

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: the existing README title and one-line product description.
- Produces: a GitHub-renderable centered header with three Shields.io badges.

- [x] **Step 1: Replace the existing title and description with the approved header**

Insert this content at the start of `README.md`:

```html
<div align="center">

# Codex Account Switcher

**Windows 上的 Codex 多账号安全切换器，支持额度查看、设备登录和托盘运行**

![platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4?style=flat)
[![built with](https://img.shields.io/badge/built%20with-.NET%209%20%C2%B7%20WPF-512BD4?style=flat)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![helper](https://img.shields.io/badge/helper-codex--auth%200.2.10-2EA44F?style=flat)](https://github.com/Loongphy/codex-auth/releases/tag/v0.2.10)

</div>
```

- [x] **Step 2: Verify the README structure**

Run:

```powershell
rg -n "img\.shields\.io/badge/" README.md
git diff --check
git diff -- README.md
```

Expected: exactly three badge URLs, no whitespace errors, and no changes below the original introductory description.

- [x] **Step 3: Verify badge endpoints**

Run:

```powershell
$urls = Select-String -Path README.md -Pattern 'https://img\.shields\.io/badge/[^)\]]+' -AllMatches |
  ForEach-Object { $_.Matches.Value }
$urls | ForEach-Object { (Invoke-WebRequest -Uri $_ -Method Head -UseBasicParsing).StatusCode }
```

Expected: three `200` responses.

- [x] **Step 4: Commit and push**

```powershell
git add README.md docs/superpowers/plans/2026-07-24-readme-header-badges.md
git commit -m "docs: add README header badges"
git -c http.proxy=http://127.0.0.1:7897 -c https.proxy=http://127.0.0.1:7897 push
```

Expected: the new commit is pushed to `origin/main`.
