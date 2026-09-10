# 24.6.1 — installatieknop herstellen

24.6.0 kon niet worden geïnstalleerd vanuit een nog draaiende 24.5.16: de nieuwe installer vroeg om een afsluitbericht dat de oude app niet kende. Daardoor brak de installatie af. Deze release herstelt die overgang.

- De installer sluit versies tot en met 24.6.0 af via hun Windows-berichtenlus als het nieuwe afsluitverzoek niet wordt verwerkt. Alleen de app in de juiste installatiemap en Windows-sessie wordt aangesproken; Darkroom moet gesloten zijn. Nieuwere apps gebruiken hun eigen afsluitafhandeling.
- Handmatig bijwerken via de bestaande knop vereist vanaf 24.6.1 geen aparte onderhoudsstap. Onbeheerde installatie blijft onderhoud vereisen.
- Een gereedstaande update toont **Update installeren**; een geblokkeerde release toont **Opnieuw controleren** met de reden.
- Bij een geweigerde installatie blijft de nieuwe updater beschikbaar voor een volgende poging. Een afgebroken installatie vermeldt de werkelijk geïnstalleerde versie.
- De installer herstart de lichtregeling als een fout optreedt nadat die is afgesloten.

Upgraden vanaf 24.5.16 kan via de bestaande updateknop zodra Darkroom gesloten is. Wie 24.6.0 al heeft geïnstalleerd kan eenmalig onderhoud starten of de nieuwe installer direct uitvoeren; die oude updater kan zijn eigen onderhoudsvoorwaarde nog niet overslaan.

De regressietest start verborgen testapps met het gedrag van 24.5.16, 24.6.0 en 24.6.1 en controleert normale beëindiging van de berichtenlus. Ook een geweigerd afsluitverzoek van een toekomstige versie, een andere installatiemap, een andere Windows-sessie en het starten van Darkroom tijdens de overgang worden gecontroleerd. Dezelfde test draait vóór publicatie in de releaseworkflow.
