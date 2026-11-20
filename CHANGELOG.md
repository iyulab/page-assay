# Changelog

All notable changes to this project are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-11-21

### Added
- `PageAssay`, the first package: `Assayer.RunAsync` drives a page in a headless Chrome, Edge or Chromium over the Chrome DevTools
  Protocol (or an endpoint the caller owns) in every viewport and colour scheme, and reports uncaught errors, unhandled
  rejections, console errors, files that did not arrive, refused requests to other hosts, engine error text on screen, low text
  contrast, unnamed fields and sideways overflow.
- `Assayer.RunStatic`: the static rules over a page's source, with no browser — `syntax`, `undefined-name`, `missing-id`,
  `duplicate-id`, `outside-reference` and `read-before-set`.
- Comparing two versions of a page: every finding has a key that survives edits elsewhere in the file and is marked new, kept or
  gone.
