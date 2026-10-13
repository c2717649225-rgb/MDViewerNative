# Code and tables

Paragraph before code, long enough to wrap in a narrow window so the relation between body text and code panels is visible.

```python
def reading_desk(pages, light="warm"):
    """A long docstring line that goes on and on to show horizontal overflow inside the code viewport of the block."""
    for page in pages:
        yield page.strip()
```

```
plain code without a language
second line
```

| Key | Description | Default value | Notes about behaviour that can be quite long |
| --- | --- | --- | --- |
| font | Body size in DIP | 16 | Ctrl + wheel changes it between 12 and 28 |
| theme | Light or dark | system | Follows the system on first run |
| toc | Pinned table of contents | off | Only pins when the margin is wide enough |

| Narrow | Table |
| --- | --- |
| a | b |
| c | d |

> Quoted code:
>
> ```json
> { "quiet": true, "desk": "serene" }
> ```

Closing paragraph after the content.
