# Inline compatibility review

## Distinct reference destinations

[spaced label][a b] and [joined label][ab] must target different URLs.

[a b]: /spaced
[ab]: /joined

## Escapes are matched literally in reference labels

[bar][foo\!] must remain text, not become a link to /unescaped.

[foo!]: /unescaped

## Invalid definitions stay visible

[foo][ref[bar]]

[ref[bar]]: /must-not-be-consumed

## Unicode emphasis boundaries

*𞋿*alpha. must keep its literal asterisks.

*中文 😀* and **bold** must still be styled.

Compare the rendered view, selection/copy and exported HTML. This fixture is not a visual acceptance certificate.
