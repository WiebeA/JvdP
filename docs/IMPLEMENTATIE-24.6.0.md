# LDR 24.6.0 — implementatie

Deze versie voert de verbeteringen uit de analyse van 10 september door in de lokale software. De code kan worden gebouwd en getest zonder aangesloten camera. De sessiekoppeling moet per booth in Darkroom worden ingesteld en met die camera worden beproefd voordat automatisch aanpassen tijdens een evenement wordt gebruikt.

## Lichtregeling

- Sensoruitval of meer dan vijf seconden zonder verse meting maakt de doelwaarde en stabiliteitsperiode ongeldig. Ook dezelfde doelwaarde moet na herstel opnieuw de volledige wachttijd doorlopen.
- De stabiliteitstijd gebruikt een monotone klok. Standaard blijft de wachttijd 60 seconden.
- Een recent waargenomen ISO krijgt voorrang op een oude bevestiging. Een bewust gekozen, door de camera ondersteunde vervangende ISO wordt apart gekoppeld aan het gevraagde doel. Bevestigingen verlopen na vijftien minuten en worden bij een geschikt rustmoment opnieuw gecontroleerd.
- Drie opeenvolgende mislukte automatische acties stoppen verdere automatische pogingen. De storing blijft zichtbaar. **Storing herstellen** wist de blokkade en vraagt verse metingen en een nieuwe ISO-controle.
- Kalibratie ondersteunt twee ADC-referentiepunten, omgekeerde sensorpolariteit, een instelbare grensmarge van 0–10 en een maximale ISO. De standaardmarge is 2 lichtpunten. Deze waarde is instelbaar en moet met de sensoropstelling worden beoordeeld.

## Profielen en bediening

Via **Verbindingen en technische details → Kalibratie en diagnose**, of het systeemvakmenu:

- Boothnaam, camera/opstelling, donker-/lichtreferentie, grensmarge en maximale ISO instellen.
- Ruwe donker- en lichtmetingen overnemen bij nieuwe firmware; bij oude firmware blijven handmatige referentiepunten en bestaande lichtwaarden bruikbaar.
- Een volledig profiel importeren/exporteren. Import is een concept tot **Opslaan**; de eigen booth-ID blijft behouden.
- Lichtverloop van maximaal 900 meetpunten bekijken, inclusief markers voor bevestigde en mislukte acties.
- Een lokaal ZIP-diagnosepakket opslaan met profiel, versies, toestand, lichtverloop en logbestanden. Er wordt niets naar een externe dienst verzonden.
- Een rustmoment van 30 seconden expliciet bevestigen voor een gecontroleerde handmatige test.
- Onderhoud starten en stoppen. Sluit Darkroom voordat onderhoud wordt gestart.

Profielen staan in `%LOCALAPPDATA%\JvdP\LightDarkroomOverlay\booth-profile.json`. De eerste start neemt de oude profiel-/wachttijd-/schermtekstbestanden over. Opslaan valideert eerst en vervangt vervolgens het complete JSON-bestand atomair. Een fout laat het vorige profiel actief. Automatisch starten/stoppen blijft in het bestaande aparte statusbestand opgeslagen.

De combinatie van booth-ID, profielrevisie, cameraomschrijving en diagnose-export vormt de lokale basis voor later centraal beheer. Een centrale server of online vlootdashboard is in deze versie niet toegevoegd.

## Darkroom-sessiekoppeling

De installer plaatst `JvdpSessionSignal.exe` naast de app. Zie [SESSION-INTEGRATION.md](SESSION-INTEGRATION.md) voor de koppeling en praktijktest.

Een geldig rustsignaal geldt 30 seconden en is gebonden aan de proces-ID én starttijd van Darkroom. `busy` verloopt nooit automatisch naar `idle`. Ontbrekende, ongeldige, toekomstige of verouderde signalen geven geen vrijgave. Bij een eerste opstart buiten Booth Mode kan de oorspronkelijke automatische voorbereiding wel plaatsvinden, mits Darkroom geen actieve sessie meldt.

De historische native navigatie is onderbouwd voor Darkroom 3.01.1434. Andere versies blokkeren automatische acties totdat de operator de actuele versie na een praktijktest bevestigt in het kalibratievenster. Die bevestiging geldt alleen voor het specifieke versienummer. De ISO-/Booth-flow moet ook op de aangesloten camera werken; versienummercontrole alleen bewijst dat niet.

## Updates en herstel

De updater controleert nog steeds bij starten en elke 30 minuten. Downloads en SHA-256-controle mogen tijdens gebruik plaatsvinden. Installatie start pas met een geldige onderhoudsvrijgave én als Darkroom in de huidige Windows-sessie gesloten is. De onderhoudsvrijgave vervalt na een uur.

Een ISO-actie en installatie gebruiken dezelfde exclusieve operatievergrendeling. De installer vraagt de geïnstalleerde app gecontroleerd te sluiten en beëindigt geen Darkroom-proces. Bij oudere appversies zonder deze sluitkoppeling moet de lichtregeling eerst via **Afsluiten** worden gesloten.

De vorige programmabestanden, versiegegevens en het boothprofiel blijven in `previous-version` staan. De installer commit pas nadat de nieuwe app via een vers, procesgebonden token heeft bevestigd dat haar berichtenlus draait en het profiel geladen is. Bij mislukken wordt de vorige complete set teruggezet. De mislukte release wordt in `blocked-release.txt` vastgelegd en niet automatisch telkens opnieuw geïnstalleerd. Een onderbroken transactie is herkenbaar en wordt bij de volgende installatierun eerst hersteld.

De bestaande SHA-256-controle blijft actief. Authenticode-ondertekening vereist een beheerd certificaat; er is geen certificaat aangemaakt of als onderdeel van deze wijziging verkregen.

## Firmware en onderhoudbaarheid

Nieuwe firmware stuurt naast de bestaande `JVDP|light=...` een aparte `JVDP2`-regel met sensor-ID, firmwareversie, raw ADC, volgnummer en uptime. Oude apps blijven de eerste regel lezen; nieuwe apps gebruiken de uitgebreidere gegevens. Wi-Fi verbinden blokkeert de USB-meetlus niet langer.

De laatst geldige COM-poort krijgt prioriteit. Herhaald afgewezen poorten worden met oplopende tussenpozen opnieuw geprobeerd; Windows-apparaatwijzigingen wissen die wachttijd. Logs roteren bij circa 2 MB naar één vorig bestand.

Sensorverwerking, actiecoördinatie, profielen, herbruikbare bedieningselementen, schermen en updateherstel zijn in afzonderlijke bronbestanden ondergebracht. De build en layouttest nemen de nieuwe bestanden mee. PlatformIO Core is in CI vastgezet op 6.1.18 en het ESP-platform op 6.10.0.

## Verificatie

- Windows-build en ESP32-C3-firmwarebuild uitgevoerd.
- Bestaande navigatie- en desktopintegratietests uitgevoerd.
- Nieuwe betrouwbaarheidstests voor sensorreset, monotone tijd, ISO-cache, protocolvalidatie, kalibratie, grensruis, profielopslag, sessiestatus, onderhoudsverval en updateherstel.
- De echte installer wordt met een geïnjecteerde fout na het vervangen van bestanden getest in een geïsoleerde map; de oorspronkelijke executable wordt op SHA-256 gecontroleerd na rollback.
- Bestaande layoutmatrix: 61 gevallen zonder gevonden geometrieproblemen. Het nieuwe venster is daarnaast op drie afmetingen gerenderd zonder een venster op het bureaublad te tonen.

De lokale firmwarebuild gebruikt uitsluitend testwachtwoorden en is niet geflasht. Er is geen nieuwe release-tag of automatische uitrol naar booths onderdeel van een gewone broncodepush. Een praktijktest met ESP, camera en de ingestelde Darkroom-events blijft nodig voor ingebruikname van de sessiekoppeling.
