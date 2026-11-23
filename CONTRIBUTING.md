# Contributing

Don't push directly to `main`, even if you're a maintainer. Create a topic branch from `main` in your fork or, if you have write access, in this repository. Push the topic branch and open a pull request targeting `main`.

For larger changes to campaign behavior or save data, open an issue first so we can agree on the expected behavior. Keep each pull request focused and explain what changed, why, and how you tested it. For gameplay changes, include the Bannerlord and Harmony versions you used and note anything you could not verify in a live campaign.

Use the [README](README.md) to build and run the smoke fixtures. If you change a campaign rule, update [SPEC.md](SPEC.md) and add or update a fixture when practical. Test live changes on a disposable save.

Keep private config, absolute local paths, dependency DLLs, build artifacts, and saves out of commits. For bug reports, share only the relevant log excerpt and redact usernames, local paths, and other private details before posting. If you don't want your email published in commit metadata, use your GitHub-provided noreply address in your local Git config before committing.
