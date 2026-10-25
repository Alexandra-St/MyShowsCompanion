# ChatGPT Notes Workflow

This workflow carries useful viewing preferences between the local MyShows Companion library and a ChatGPT discussion. It is manual: the app does not read ChatGPT conversations, call an AI API, or generate notes automatically.

1. Import a MyShows profile workbook into MyShows Companion.
2. Use **Export for ChatGPT** to save `myshows-chatgpt.json`.
3. Share that export in a ChatGPT discussion if desired, and discuss the titles or broader preferences.
4. Ask ChatGPT to prepare a `myshows-notes.json` file using [the v1 schema](myshows-notes.schema.json).
5. Review the notes, save the file, and choose **Импортировать заметки ChatGPT** in the app.

## What to remember

ChatGPT can create two kinds of notes:

### Entity notes

An entity note belongs to one series or movie. It can record why the user dropped a title, why a rating was high or low, specific aspects they liked or disliked, reactions to characters, plotlines, pacing or atmosphere, meaningful viewing context, or a change of opinion.

Prefer a concrete reason, for example:

> Dropped in November 2026 because long scenic sequences repeatedly killed the pacing.

Every entity note must use the matching `mediaType` and local `mediaId` from the **current** `myshows-chatgpt.json`. Do not identify a title by name alone. If duplicate titles make the match uncertain, omit the entity note until the correct exported ID is clear.

### Taste notes

A taste note captures a general, potentially reusable preference that is not about one particular title. For example:

> Prefers psychologically complicated relationships over conventional romance.

## Selection and confidence

Do not create a note for every user message. Keep only information that could help with future recommendations. Prefer concise, specific observations over broad judgments.

Do not save throwaway jokes unrelated to taste, plot summaries, temporary technical questions, widely known facts about a title, unconfirmed guesses, or several near-duplicate phrasings of the same observation. A note should reflect an explicit user reaction or a clear implication of the discussion. If an interpretation is debatable, leave it out rather than presenting it as fact. The JSON has no confidence score.

## `myshows-notes.json` v1

The root object requires `schemaVersion`, `entityNotes`, and `tasteNotes`. Either array may be empty. Use ISO 8601 timestamps with a timezone, such as `2026-11-10T20:30:00+00:00`. `source` is a non-whitespace string; ChatGPT-created notes should use `chatgpt`.

```json
{
  "schemaVersion": 1,
  "entityNotes": [
    {
      "mediaType": "series",
      "mediaId": "GUID_FROM_MYSHOWS_CHATGPT_EXPORT",
      "text": "Dropped because the pacing became repetitive.",
      "createdAt": "2026-11-10T20:30:00+00:00",
      "source": "chatgpt"
    }
  ],
  "tasteNotes": [
    {
      "text": "Likes morally ambiguous characters more than conventional heroes.",
      "createdAt": "2026-11-10T20:30:00+00:00",
      "source": "chatgpt"
    }
  ]
}
```

The schema checks the file's shape and field formats. It cannot check whether an entity ID exists in the app's current database; the importer checks that before saving anything. If any entry is invalid, the entire file is rejected. Imports are append-only: existing notes are neither edited nor deleted. Duplicate entity notes are skipped when media type, media ID, normalized text and source match. Duplicate taste notes are skipped when normalized text and source match. Text normalization trims its ends and converts line endings to `\n`; matching is case-sensitive and does not use `createdAt`.

[`samples/myshows-notes-sample.json`](../samples/myshows-notes-sample.json) is synthetic. Its entity GUID is a placeholder and is not guaranteed to match the sample workbook or any local database. Replace it with the exact local ID from the current library export before importing.

The app exports notes in ChatGPT library schema v2. Import and export remain local file operations; sharing either file with ChatGPT is an explicit user action.
