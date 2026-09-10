# 24.6.3 — Camera Settings en ISO bevestigen vóór Booth Mode

De pagina-aansturing kon vervolgopdrachten versturen terwijl een eerdere opdracht nog niet was verwerkt. Daarnaast kon de herstelroute Booth Mode heropenen na een mislukte ISO-aanpassing. Dat past bij het gemelde gedrag: de booth lijkt klaar, maar Camera Settings was niet de laatst gebruikte pagina en de ISO is niet aangepast.

Deze release verandert de volgorde:

1. Elke instellingenopdracht moet zijn verwerkt voordat de volgende opdracht wordt verstuurd. Er is een begrensde wachttijd, in plaats van een reeks pagina-opdrachten achter elkaar.
2. Een keuzelijst met nummer 107 is op zichzelf geen bewijs voor Camera Settings. Ook de bijbehorende camerabediening voor modus, diafragma en sluitertijd moet zichtbaar zijn.
3. De ISO wordt op de actuele keuzelijst gekozen. Ook Enter moet verwerkt zijn; alleen een gemarkeerde waarde geldt niet als bevestiging.
4. De app leest de ISO terug met gesloten keuzelijst, opent Camera Settings opnieuw en leest de waarde opnieuw terug.
5. Direct vóór Start Booth volgt nog een controle op de actieve Camera-pagina en ISO. Daarna wordt Start één keer verstuurd en moet het volledige Booth-scherm verschijnen.

Bij een ontbrekende Camera-pagina of onbevestigde ISO wordt Booth Mode niet opnieuw gestart door de herstelroute. De app toont een fout. De achtergrondcontrole neemt tijdens een ISO-actie of een geopende keuzelijst geen tussentijdse ISO over als bevestiging.

De regressietests bevatten trage pagina-opdrachten, een paginawissel vlak vóór Start, een gemarkeerde maar onbevestigde ISO en een ISO die na heropenen terugvalt. Er is daarnaast een aparte test met echte Windows-vensters en native ComboBoxes in een eigen proces buiten de beeldschermen; die controleert ook de Enter-bevestiging en een misleidende keuzelijst op een andere pagina. De aangesloten camera op de andere booth-pc is hiermee niet fysiek getest.

Sluit Darkroom en installeer 24.6.3 via de updateknop. Laat na het starten van Darkroom de lichtregeling een ISO-actie uitvoeren; bij succes hoort Camera Settings na het verlaten van Booth Mode de laatst gebruikte instellingenpagina te zijn.
