# 24.6.4 — Darkroom bedienen met een fallback voor afwijkende vensters

De melding "Het Darkroom-hoofdvenster kon niet eenduidig worden gevonden" ontstond vóór de eerste ISO-opdracht. De herkenning stelde eisen aan interne toolbar-elementen en venstertitels die niet voor elke Darkroom-versie of vensterindeling gelden. Een tweede toolbar in een hulpvenster kon bovendien als een tweede hoofdvenster worden geteld.

## Analyse en workaround

- De app zoekt het DarkroomBooth-proces in dezelfde Windows-sessie. Ze bedient native Windows-vensters met de eerder gebruikte Darkroom-opdrachten: Originals 543, Settings 545, volgende instellingenpagina 662 en Start Booth 33776. Dit vereist geen herkenning van pixels of een uitleesbare volledige Darkroom-schermstructuur.
- Voorheen accepteerde de vensterherkenning alleen één venster met toolbar 4083, of één zelfstandig venster met "Darkroom" in de titel. Versie 24.6.4 brengt de oudere route via het Windows-hoofdvenster terug als fallback. Een eventtitel zonder productnaam en ontbrekende toolbar blokkeren die route niet meer. Hulpvensters met dezelfde eigenaar worden samengenomen; onzichtbare framework-toolvensters worden niet als commandobestemming gebruikt.
- Camera Settings werd sinds 24.6.3 alleen geaccepteerd als alle drie de naburige cameravelden 104, 105 en 106 zichtbaar waren. Als die indeling ontbreekt, accepteert 24.6.4 ook ISO-keuzelijst 107 met een native ISO-label en minstens drie numerieke ISO-waarden. De app hoeft de overige cameravelden dan niet te kunnen herkennen.
- Als het hoofdvenster zelf in een volledig Booth-scherm is veranderd, kan de app dat eerst verlaten en daarna de editor opnieuw vinden. Een verdwenen hoofdvenster wordt opnieuw gezocht. Het diagnoselog vermeldt de gekozen route en gevonden vensters, zodat een fout niet langer alleen een algemene herkenningsmelding oplevert.

Opdrachten blijven één voor één verwerkt. ISO wordt met Enter bevestigd en na opnieuw openen teruggelezen voordat Booth Mode start. Deze controles voorkomen de eerder gemelde situatie waarin de booth startte met de oude ISO. De automatische regeling blijft werken zonder verplichte sessiekoppeling.

## Grenzen en controle

De private Darkroom-commandonummers en ISO-control 107 blijven versieafhankelijk; dit is geen officiële Darkroom-integratie. De app leest de ISO terug uit Darkroom, niet rechtstreeks uit de camera. Een volledig onleesbaar ISO-veld of een onbekend blokkerend dialoogvenster kan nog steeds een actie stoppen. De oude scan naar private geheugenadressen wordt niet teruggezet: die hing juist af van de exacte Darkroom-build.

De tests draaien zowel met meerdere toolbar-vensters als met een toolbarloos eventvenster zonder "Darkroom" in de titel. De laatste variant mist ook de drie verwachte cameravelden. Echte Windows-keuzelijsten testen selectie, Enter, opnieuw openen, trage opdrachten en een geweigerde ISO-wijziging. Daarnaast worden venstereigenaars, verborgen toolvensters, alternatieve hoofdvensters en ongeldige eigenaarsketens getest. Darkroom 2.01.1354 en de camera op de andere booth-pc zijn hier niet fysiek getest; de specifieke vensterindeling daar is niet vastgesteld.

Sluit Darkroom op de booth-pc en installeer 24.6.4 via de updateknop. Open vervolgens het event en kies Start Booth in de lichtregeling.

Lokale verificatie: 399 navigatiecontroles, 38 native venster/ComboBox-controles, 25 desktopintegratiecontroles, 109 betrouwbaarheidscontroles, de installer-terugroltest en 45 updatecontroles geslaagd. De headless layoutmatrix heeft 61 gevallen zonder afwijkingen. Een extra zichtbare layouttest op 150% schaal meldt in het ongewijzigde Licht-en-ISO-paneel bij 1000×720 twaalf meldingen over uitstekende verwijderknoppen/spacers; bij de overige vensterformaten niet. Dat afzonderlijke layoutpunt is niet in deze Darkroom-fix aangepast.
