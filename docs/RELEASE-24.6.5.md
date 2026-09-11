# 24.6.5 — Bijwerken terwijl Darkroom openblijft

Darkroom afsluiten was een beperking van onze updateflow, geen technische eis van Darkroom. De installer vervangt de lichtregeling, updater en bijbehorende JvdP-bestanden; hij vervangt geen Darkroom-bestanden.

Vanaf een geïnstalleerde versie 24.6.5 kan de knop **Update installeren** de lichtregeling bijwerken terwijl Darkroom openblijft. De updater blokkeert niet meer op het Darkroom-proces, de installer heeft die algemene eis niet meer en de lichtregeling accepteert het afsluitverzoek ook wanneer Darkroom draait. De update stuurt geen afsluit-, herstart- of navigatieopdracht naar Darkroom.

Een lopende ISO-aanpassing moet eerst klaar zijn. De bestaande operatievergrendeling voorkomt dat de installer de lichtregeling midden in die aanpassing vervangt. Als een aanpassing bezig is, blijft de app draaien en kan de update daarna opnieuw worden gestart. De nieuwe app wacht met ISO-aanpassingen tot de installer de installatie heeft afgerond; daarna gelden weer de normale metingen, stabiliteitstijd en instellingen. Opstartcontrole en terugrol blijven beschikbaar.

Updates worden nog steeds pas geïnstalleerd na een klik op de installatieknop of tijdens bewust ingeschakeld onderhoud. Onderhoud kan nu ook met Darkroom open worden gestart; het pauzeert alleen de JvdP ISO-regeling.

## Eenmalige overstap vanaf 24.6.4 of ouder

De oude updater en lichtregeling bevatten hun eigen blokkade nog. Wie die updater gebruikt, moet voor deze ene overstap nog Darkroom sluiten. Daarna is dat voor volgende updates niet meer nodig.

Darkroom kan ook tijdens deze eerste overstap openblijven:

1. Download de officiële installer van deze release.
2. Sluit alleen **JvdP Lichtregeling** via het pictogram bij de Windows-klok → **Afsluiten**. Wacht zo nodig tot een lopende ISO-aanpassing klaar is.
3. Start de gedownloade installer. Darkroom kan gewoon openblijven.

De installer forceert geen afsluiting van een oude of niet-reagerende lichtregeling. Als de oude versie nog draait terwijl Darkroom openstaat, geeft hij bovenstaande instructie.

## Verificatie

De regressietests controleren de afsluitprotocollen voor oude, huidige en toekomstige versies, het achterwege blijven van de Darkroom-sluitcontrole vanaf 24.6.5, weigering tijdens een actieve ISO-aanpassing, installatiemap en Windows-sessie, en normaal afsluiten zonder geforceerd beëindigen van de lichtregeling. De echte actietoelating is getest met een lopende update en na het afronden daarvan. De bestaande installer-terugroltest blijft onderdeel van de controle.

De tests gebruiken geïsoleerde testprocessen en bestanden. De andere booth-pc met de aangesloten camera is hier niet fysiek getest.
