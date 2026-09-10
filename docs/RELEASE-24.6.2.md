# 24.6.2 — automatische regeling herstellen

24.6.0 en 24.6.1 blokkeerden automatische ISO-aanpassingen zonder recent rustsignaal en bij Darkroom-versies buiten een vaste lijst. Daardoor werkte de bestaande regeling onder meer niet meer op een booth met Darkroom 2.01.1354. Deze release herstelt de bestaande automatische regeling.

- Een rustsignaal is standaard niet meer verplicht. Bestaande profielen werken zonder nieuwe instellingen.
- De extra sessiekoppeling kan bewust worden ingeschakeld via **Kalibratie en diagnose → Wachten op een Darkroom-rustsignaal** nadat de Darkroom-events zijn ingesteld en getest.
- Een ontbrekende praktijktest voor een Darkroom-versie is informatief. De software blokkeert niet alleen vanwege het versienummer en markeert de versie ook niet automatisch als getest.
- Verse sensormetingen, stabiliteitstijd, pauze, onderhoud en foutafhandeling blijven van toepassing. Een ontvangen signaal dat er een fotosessie bezig is, blijft de regeling blokkeren.
- De app blijft de daadwerkelijke Darkroom-bediening controleren en de gekozen ISO teruglezen.

Sluit Darkroom en gebruik de updateknop om 24.6.2 te installeren. Start Darkroom daarna weer. Bij de standaardinstelling hoef je geen rustmoment of compatibiliteitsbevestiging meer te geven.

De regressietests controleren de blokkadelogica expliciet met versienummers 2.01.1354 en 2.01.1354.0, oude profielen zonder nieuwe optie en de optionele strikte sessiekoppeling. Dat is geen praktijktest met de camera op de betreffende booth. Zonder ingestelde sessiekoppeling kent de app de gastensessiegrenzen niet; de bestaande afdekking en native navigatie blijven dan de werkwijze.
