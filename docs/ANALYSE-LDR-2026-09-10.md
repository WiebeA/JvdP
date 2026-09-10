# Analyse LDR-software — 10 september 2026

> Dit document beschrijft de stand vóór implementatie. Zie [de implementatienotities](IMPLEMENTATIE-24.6.0.md) voor de uitgevoerde wijzigingen.

De bestaande architectuur is bruikbaar om op voort te bouwen. De belangrijkste volgende stap is voorspelbaar gedrag tijdens een evenement: alleen ingrijpen op een geschikt moment, uitsluitend verse meetgegevens vertrouwen en bij fouten helder herstellen. Daarna volgen kalibratie, betere diagnose en eenvoudiger beheer van meerdere booths.

## Onderzochte stand

- Repository: `Programming/LDR`, branch `main`, commit `c091b5a87f0f402d59944d22fa848044bfbfba61`, versie **24.5.16**. De opgevraagde remote `main` wijst naar dezelfde commit.
- De geïnstalleerde Windows-app op deze pc is ook 24.5.16. De app en updater draaien. De lokale updaterstatus noemt v24.5.16 als beschikbare versie.
- In de recente lokale log worden COM8, COM3 en COM5 herhaaldelijk geopend en afgewezen omdat er geen geldige JVDP-regel binnenkomt. Dit bewijst geen hardwaredefect of fysieke afwezigheid van de sensor; het bewijst wel dat deze app op dat moment geen geldige sensorstream ontvangt.
- Tijdens de procescontrole is geen Darkroom-proces aangetroffen. Deze analyse bevat daarom geen nieuwe praktijktest met camera, sensor en gastenflow.
- Productiebroncode, geïnstalleerde software en boothinstellingen zijn niet aangepast. Alleen build- en analysebestanden zijn aangemaakt.

## Wat er al is

| Onderdeel | Huidige werking | Beoordeling |
|---|---|---|
| ESP32-C3 | LDR op GPIO0; negen ADC-metingen per meetronde, uitschieters verwijderd; meting iedere 100 ms; lichtwaarde 0–100 iedere seconde via USB | Eenvoudige en duidelijke taakverdeling |
| Windows-app | Automatische COM-detectie met JVDP-validatie, achtergrondcontroles, systeemvak, dashboard en handmatige override | Veel operationele basisfuncties zijn aanwezig |
| Lichtregeling | Zes standaardbanden, ISO 3200 tot 400; lokaal eigen profiel; instelbare stabiliteitstijd 5–300 seconden, standaard 60 | Goed configureerbaar, maar grensgedrag en sensorherstel verdienen aandacht |
| Darkroom-integratie | Native vensters en controls; gerichte navigatie; controle van zichtbare ISO; herstel en presentatie van Booth Mode | Beter afgebakend dan de oudere implementaties; blijft afhankelijk van Darkrooms interne bediening |
| Distributie | Installer per Windows-gebruiker, automatisch starten, stable/test-kanaal, SHA-256-controle van updates | Bruikbare basis; installatiemoment en terugval ontbreken |
| Testen | Navigatiesimulatie, installatie-/instantiecontroles en bestaande layoutmatrix | Sterk op eerdere navigatieproblemen; sensorlogica en updateonderbrekingen zijn minder afgedekt |

De ESP meet een relatieve ADC-waarde. De schaal 0–100 is geen luxmeting en is momenteel niet gekalibreerd per sensoropstelling. Het standaardprofiel is centraal in de applicatiecode vastgelegd; een wijziging wordt via een software-release verspreid. Een lokale profielwijziging wordt niet automatisch naar andere booths gepubliceerd.

## Eerst aanpakken

### 1. Aanpassen tussen fotosessies, met behoud van automatisch opstarten

**Vaststelling uit de code:** zodra het doel lang genoeg stabiel is en Darkroom draait, kan de automatische actie starten. De navigatie verlaat een zichtbare Booth Mode. Er is geen afzonderlijke blokkade voor aftellen, fotograferen, keuze-/afleveringsschermen of een actieve gastensessie.

**Risico:** een lichtverandering die voldoende lang aanhoudt kan tot een onderbreking tijdens een sessie leiden. De fullscreen melding verbergt de navigatie, maar maakt die onderbreking niet ongedaan. Dit is een codebevinding, geen hier waargenomen mislukte fotosessie.

**Voorstel:** onderscheid minimaal `opstarten`, `gereed voor gasten`, `sessie bezig`, `aanpassing wacht`, `ISO aanpassen` en `storing`. Laat bij de eerste opstart de bestaande automatische voorbereiding intact. Bewaar tijdens een sessie het nieuwe doel en pas het pas toe in een bevestigd rustmoment. Controleer dan opnieuw of het doel nog geldig en stabiel is. Ontbrekende of verouderde sessiestatus mag niet automatisch als een rustmoment gelden.

Darkroom documenteert dat Device Control per sessiefase een script of extern programma kan uitvoeren. Dat is een concrete route om een kleine lokale helper sessiesignalen aan de lichtregeling te laten doorgeven. De exacte beschikbare fases, ook na annuleren en bij herstarten, moeten op de gebruikte Darkroom-versie worden getest. Dit is geen bewijs van een officiële directe ISO-API. [Darkroom: Device Control](https://support.darkroomsoftware.com/portal/en/kb/articles/device-control-phidget).

**Acceptatie:** geen ISO-navigatie tijdens een actieve sessie; initialisatie blijft automatisch mogelijk; na een sessie wordt alleen een nog geldig doel toegepast.

Bron: [automatische start](https://github.com/WiebeA/JvdP/blob/c091b5a87f0f402d59944d22fa848044bfbfba61/pc-overlay/LightDarkroomOverlay.cs#L4584), [Booth Mode verlaten](https://github.com/WiebeA/JvdP/blob/c091b5a87f0f402d59944d22fa848044bfbfba61/pc-overlay/DarkroomNavigation.cs#L64).

### 2. Updates uitstellen tot onderhoud en terugrollen bij mislukken

**Vaststelling uit de code:** de updater controleert direct bij starten en daarna iedere 30 minuten. Bij een nieuwe release start hij meteen de installer. De installer beëindigt de app en updater zonder overleg met een lopende ISO-actie. Bestanden worden afzonderlijk vervangen en tijdelijke backups meteen verwijderd. Er is geen terugval naar de vorige complete versie na een mislukte opstart.

**Voorstel:** downloaden en verifiëren mag op de achtergrond. Installeren gebeurt na afloop van een evenement of bij een expliciet onderhoudsmoment. De app geeft eerst toestemming via een technisch signaal dat er geen actie loopt, slaat toestand op en sluit gecontroleerd. Bewaar de vorige complete versie en herstel die als de nieuwe app geen gezond opstartsignaal geeft. Probeer een afgewezen versie niet bij elke controle opnieuw.

**Acceptatie:** een beschikbare update onderbreekt geen ISO-actie of gastensessie; een geforceerde installatiefout herstelt een werkende vorige versie; boothprofielen blijven behouden.

Bron: [direct installeren](https://github.com/WiebeA/JvdP/blob/c091b5a87f0f402d59944d22fa848044bfbfba61/updater/JvdpAutoUpdater.cs#L272), [processen stoppen](https://github.com/WiebeA/JvdP/blob/c091b5a87f0f402d59944d22fa848044bfbfba61/installer/ProcessOperations.cs#L14), [bestanden vervangen](https://github.com/WiebeA/JvdP/blob/c091b5a87f0f402d59944d22fa848044bfbfba61/installer/InstallOperations.cs#L148).

### 3. Twee aangetoonde fouten in de beslislogica oplossen

**Sensoronderbreking:** `CloseSerial` zet de verbinding op niet gereed, maar behoudt lichtwaarde, doel-ISO en begin van de stabiliteitsperiode. Komt na herverbinden dezelfde ISO-band binnen, dan begint `SerialDataReceived` geen nieuwe periode. Tijd zonder metingen kan daardoor meetellen als stabiliteit.

Voorbeeld: het doel was 20 seconden stabiel, de sensor valt 90 seconden weg en komt terug in dezelfde band. De app kan het doel meteen als voldoende stabiel behandelen. Voorstel: iedere onderbreking of te oude meting maakt de lopende stabiliteitsperiode ongeldig; herverbinden vereist opnieuw de volledige periode met verse metingen. Gebruik voor verstreken tijd een monotone klok, zodat een wijziging van de Windows-klok de regeling niet beïnvloedt.

**Oude ISO-status:** `IsTargetIsoAlreadySet` accepteert een eerder toegepast doel bij hetzelfde Darkroom-proces, ook als een nieuwere waarneming een andere ISO meldt. In een geïsoleerde controle met eerder toegepast 1600 en recent waargenomen 800 retourneert de echte methode toch `true` voor doel 1600.

Voorstel: een recente tegenstrijdige waarneming krijgt voorrang; maak de cache ongeldig bij relevante camera-/eventwissels. Bewaar gewenste ISO en daadwerkelijk bevestigde ISO afzonderlijk. Dat is ook nodig wanneer de camera het gevraagde getal niet ondersteunt en de app een nabijgelegen ISO kiest. Geef oudere waarnemingen een zichtbaar tijdstip en plan verificatie op een geschikt moment, zonder voor elke lichtcontrole Booth Mode te verlaten.

**Acceptatie:** na een onderbreking begint de teller opnieuw; een recente ISO 800 kan niet worden overruled door een oude cache met 1600; een bewust gekozen vervangende ISO veroorzaakt geen eindeloze herhaalacties.

Bron: [verbinding sluiten en regels ontvangen](https://github.com/WiebeA/JvdP/blob/c091b5a87f0f402d59944d22fa848044bfbfba61/pc-overlay/LightDarkroomOverlay.cs#L4259), [ISO-cache](https://github.com/WiebeA/JvdP/blob/c091b5a87f0f402d59944d22fa848044bfbfba61/pc-overlay/LightDarkroomOverlay.cs#L4602). De gedragprobes staan in `artifacts/analysis-2026-09-10/`.

## Daarna verbeteren

### 4. Rustiger lichtregeling en kalibratie per booth

Rond bijvoorbeeld 16/17 wisselt het huidige doel direct tussen ISO 3200 en 2500. De wachttijd voorkomt snelle acties, maar bij herhaalde grenspassages blijft de teller opnieuw beginnen. Voeg een kleine instelbare marge rond de bandgrenzen toe: pas omschakelen als de meting duidelijk aan de andere kant ligt. Bepaal de marge en eventuele filtering over meerdere seconden met gemeten lichtreeksen; behoud 60 seconden als uitgangspunt tot praktijkgegevens een andere keuze onderbouwen.

Maak daarnaast een eenmalige kalibratiestap voor donker-/lichtbereik en controleer met testfoto's welke ISO bij de betreffende camera, belichting en sensorpositie past. Voeg bij het profiel een camera-/boothnaam, versie, maximale ISO en de werkelijk ondersteunde ISO-keuzes toe. Een profielpreview is al aanwezig; bouw daarop voort met import/export en meetgegevens uit de praktijk.

Breid het seriële protocol compatibel uit met sensor-ID, firmwareversie, ruwe ADC-waarde, volgnummer en uptime. De huidige parser accepteert niet zomaar extra velden: zowel de PC-parser als firmware moeten bewust naar een versieerbaar protocol worden uitgebreid. Blijf oude `JVDP|light=...`-regels ondersteunen tijdens de overgang.

Acceptatie: grensruis veroorzaakt geen voortdurende resets; echte langdurige lichtverandering wordt wel verwerkt; oude en nieuwe firmware kunnen tijdens uitrol naast elkaar gebruikt worden.

### 5. Bediening die vertelt wat er gebeurt en waarom

Het huidige dashboard heeft al lichtwaarde, doel-ISO, actuele ISO in details, countdown, profielinstellingen en een pauzeknop. De winst zit in duidelijkere betekenis:

- Toon gewenste ISO en laatst bevestigde camera-ISO samen, met tijdstip en eventuele afwijking.
- Toon de echte reden voor wachten: licht nog niet stabiel, sessie bezig, sensorverbinding weg, onderhoud of camerafout.
- Maak storingen blijvend zichtbaar tot herstel of bevestiging. Een mislukte automatische actie krijgt nu opnieuw een poging na minimaal 30 seconden of de ingestelde stabiliteitstijd; er is geen maximum aantal opeenvolgende pogingen. Voeg een begrensd aantal retries toe en pauzeer daarna alleen de lichtregeling met een concrete herstelactie.
- Corrigeer de huidige melding dat Booth Mode eerst handmatig moet worden gestart: de automatische startlogica verlangt dat inmiddels niet meer.
- Maak het opslaan van instellingen betrouwbaar: eerst valideren en veilig naar schijf schrijven, dan pas als opgeslagen tonen. Momenteel worden schrijffouten alleen gelogd en kan de bovenliggende flow alsnog `Settings saved` loggen.

### 6. Diagnose vanuit de app en minder onnodige COM-pogingen

Er is al een PowerShell-diagnosescript. Maak daarvan een knop in de app die een lokaal diagnosepakket produceert met app-/firmware-/Darkroomversie, verbindingstoestand, profiel, laatste acties en fouten. Voeg een korte lichtgrafiek toe met markers bij doelwijzigingen en bevestigde ISO-aanpassingen.

De huidige COM-detectie geeft ESP-/USB-poorten al prioriteit, maar blijft andere poorten herhaaldelijk proberen. Onthoud de identiteit van de laatst geldige sensor, reageer op apparaatwijzigingen en vertraag herhaalde vergeefse scans. Houd een brede fallback beschikbaar. Roteer logs en vat identieke verbindingsmeldingen samen. De huidige lokale log is circa 6,5 MB en bevat veel herhaalde zoekmeldingen; dit is vooral een diagnose- en onderhoudsverbetering.

### 7. Technische structuur en compatibiliteit expliciet maken

`LightDarkroomOverlay.cs` bevat 7.503 regels en combineert vensters, profielen, opslag, seriële data, beslislogica en de ISO-actie. `DarkroomNavigation` en `LightCheckCycle` zijn al afzonderlijke onderdelen. Splits de rest geleidelijk op in sensorverbinding, lichtregeling, profielopslag, actiecoördinatie en schermen. Leg gedeelde toestand vast in consistente snapshots, met één eigenaar voor actieovergangen.

Voeg tests toe voor sensoruitval/herstel, grensruis, profielwijzigingen, camerawissels, oude versus nieuwe ISO-waarnemingen, volle/onbeschrijfbare opslag en mislukte updates. Het doel is dekking van herkenbare boothproblemen.

De Darkroom-commandonummers zijn intern en de documentatie verwijst naar analyse van Darkroom 3.01.1434. Registreer en test expliciet de ondersteunde Darkroom-/cameracombinaties. Behandel een onbekende versie eerst als onbewezen; vertrouw niet alleen op een overeenkomstig control-ID. Pin ook de gebruikte firmware-toolchain en leg releaseherkomst vast. Ondertekening van Windows-uitvoer is een verdere verbetering voor distributie.

### 8. Beheer van meerdere booths als volgende fase

Een centraal overzicht kan later per booth firmware-/appversie, laatste contact, actief profiel, laatste succesvolle aanpassing en storing tonen. Introduceer eerst een vaste booth-ID en versieerbare profielen. Gebruik de bestaande test-/stablekanalen om veranderingen eerst op een proefbooth te valideren en daarna breder uit te rollen. Een centrale beheerlaag is pas de volgende stap nadat lokaal herstel en updategedrag betrouwbaar zijn.

## Voorgestelde uitvoering

| Volgorde | Werkpakket | Resultaat |
|---|---|---|
| 1 | Stabiliteitsreset, ISO-cache, duidelijke bewaarfouten en begrensde retries | Kleine gerichte correcties met reproduceerbare controles |
| 2 | Updatecoördinatie, onderhoudsmoment en terugval | Releases kunnen een evenement niet onverwacht onderbreken |
| 3 | Sessiestatus via Darkroom onderzoeken en valideren op één echte booth | ISO-aanpassingen gebeuren op een bevestigd geschikt moment |
| 4 | Kalibratie, bandmarges, protocolinformatie en betere diagnose | Betere afstelling en sneller problemen vinden |
| 5 | Geleidelijke opsplitsing en beheer van meerdere booths | Onderhoudbare uitbreiding en gecontroleerde uitrol |

De opsplitsing van code kan tijdens deze werkpakketten plaatsvinden. Een volledige herschrijving is voor de gevonden problemen niet nodig.

## Uitgevoerde verificatie en grenzen

- `build.ps1 -SkipFirmware`: geslaagd. Drie compilerwaarschuwingen over ongebruikte velden; geen compileerfouten.
- `test-darkroom-navigation.ps1`: **373 assertions geslaagd**.
- `test-desktop-integration.ps1`: **23 assertions geslaagd**, inclusief installatie in een geïsoleerde testmap.
- Twee aanvullende gedragprobes tegen de zojuist gebouwde echte methoden bevestigen het behoud van de stabiliteitsperiode bij `CloseSerial` en het onterecht vertrouwen van de oude ISO-cache. Hierbij zijn geen Form-constructors, echte COM-poorten of Darkroom-acties uitgevoerd. Dit demonstreert de bestaande fouten; het zijn geen aangebrachte fixes.
- De eerder aanwezige layout-QA en testcode zijn gelezen; de volledige layoutmatrix is in deze analyse niet opnieuw uitgevoerd en er is geen nieuwe visuele beoordeling van de live app gedaan.
- Firmware is gelezen, maar niet opnieuw gebouwd of geflasht. De bestaande `firmware.bin` in de buildmap is geen nieuw geverifieerde firmwarebuild.
- Voor de volledige keten blijft een praktijktest nodig met de gebruikte ESP, camera en Darkroom-versie, inclusief sessie annuleren, USB los/vast, camerawissel en herstel na een mislukte update.
