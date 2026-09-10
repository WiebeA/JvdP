# Darkroom sessiesignalen

Darkroom kan via Device Control op sessiefases een extern programma aanroepen. De leverancier beschrijft die mogelijkheid in [Device Control](https://support.darkroomsoftware.com/portal/en/kb/articles/device-control-phidget). De beschikbare fases en de afhandeling van externe programma's moeten op de gebruikte Darkroom-build worden gecontroleerd.

## Instellen op een booth

Programma: `%LOCALAPPDATA%\JvdP\LightDarkroomOverlay\JvdpSessionSignal.exe`.

| Moment | Parameter | Betekenis |
|---|---|---|
| Vóór een nieuwe gastensessie | `busy` | Blokkeer automatische ISO-aanpassingen |
| Nadat de complete gastensessie is afgerond en het startscherm weer beschikbaar is | `idle` | Geef maximaal 30 seconden een rustmoment vrij |
| Na annuleren, pas wanneer het startscherm weer beschikbaar is | `idle` | Herstel na een afgebroken sessie |

Geef `idle` niet na iedere foto, tussentijds aftellen of terwijl gasten nog hun keuze/aflevering afhandelen. Ook video-, survey- en alternatieve flows moeten de volledige gastensessie afdekken. Als de gebruikte build geen geschikt annuleren-/eindmoment beschikbaar heeft, houd automatisch aanpassen in Booth Mode geblokkeerd en gebruik een gecontroleerd handmatig rustmoment.

De helper schrijft direct een atomair lokaal signaal en wacht niet op de ISO-operatievergrendeling. Dat voorkomt een deadlock als Darkroom zelf op het externe programma wacht. De app controleert de status vóór de actie, na het tonen van de afdekking en vóór het wijzigen van ISO. Het afdekscherm blokkeert normale schermbediening tijdens navigatie.

**Beperking:** dit zijn procesgebonden sessiesignalen, geen door Darkroom gegarandeerde atomaire reservering van de camera. Externe triggers die onafhankelijk van het afdekscherm nieuwe sessies kunnen starten, moeten in de praktijktest worden meegenomen. Het verkrijgen van een vers `idle`-signaal bewijst op zichzelf niet dat elke mogelijke externe trigger uitgesloten is.

## Praktijktest voor ingebruikname

1. Start Darkroom buiten Booth Mode en controleer automatische voorbereiding na stabiele metingen.
2. Start een gastensessie. Het diagnosevenster moet `Fotosessie bezig` tonen.
3. Wijzig tijdens aftellen en fotograferen het licht voldoende lang. De software mag de sessie niet verlaten of ISO wijzigen.
4. Rond de complete sessie af. De software mag bij een stabiel doel binnen het nieuwe rustmoment aanpassen en moet het volledige Booth-startscherm herstellen.
5. Herhaal bij annuleren, video, enquêtes, keuze-/afleveringsschermen en alle gebruikte hardware- of toetsenbordtriggers.
6. Laat een eind-/annuleersignaal bewust weg. De app moet geblokkeerd blijven, niet na een timeout alsnog ingrijpen.
7. Herstart Darkroom. Oude sessiesignalen moeten worden genegeerd.
8. Test een USB-onderbreking: na herstel moet de volledige stabiliteitstijd opnieuw beginnen.

Leg Darkroom-build, cameramodel, profiel en uitkomst vast in het boothdiagnosepakket. Alleen na deze proef geldt de combinatie als getest.
