# 24.6.6 — Darkroom-opdrachten via de normale berichtenwachtrij

De gemelde fout was: `Darkroom heeft opdracht 0x111 niet binnen 8000 ms verwerkt`, vóór het versturen van de ISO-keuze. `0x111` is `WM_COMMAND`: de vorige versie wachtte synchroon op een instellingenopdracht. De melding identificeert die aanroep, maar bewijst op zichzelf niet waarom de Darkroom-thread intern vastloopt.

## Wijziging

- Instellingenopdrachten gaan weer via `PostMessage`, zoals in de eerdere aansturing. De code roept Darkrooms instellingenhandler niet meer synchroon aan. Het log vermeldt vooraf ook het concrete commandonummer: Originals 543, Settings 545 of volgende pagina 662.
- Na een opdracht wordt een stabiele verandering in de zichtbare native vensterstructuur afgewacht. Er wordt geen nieuwe Next-opdracht toegevoegd zolang die verandering ontbreekt. Er is één uitzondering voor het idempotente Originals-commando: die pagina kan al geselecteerd zijn. Na 1,2 seconde zonder verandering, met een reagerend venster, mag één Settings-opdracht volgen. Dit is een fallback, geen bevestiging dat Darkroom de eerste opdracht heeft afgehandeld.
- Het openen van de ISO-keuzelijst, de pijltoetsen en Enter gaan eveneens door de gewone berichtenwachtrij. Na iedere stap wordt de verwachte toestand teruggelezen. Alleen uitleesopdrachten mogen nog de synchrone helper gebruiken.
- Een timeout stopt verdere automatische pogingen meteen. De gebruiker kan na controle van Darkroom via **Kalibratie en diagnose → Herstellen** weer verder. Een al verzonden Windows-opdracht kan bij een timeout niet worden ingetrokken; daarom worden geen herhaalopdrachten toegevoegd.
- De ISO-controle na opnieuw openen van Camera Settings blijft behouden. Een mislukte keuze wordt niet als geslaagd behandeld en start Booth Mode niet alsnog.

Dit vermijdt de synchrone aanroepcontext als mogelijke oorzaak van een vastloper. Het is geen officiële Darkroom-API en de vensterstructuur kan per versie verschillen. De camera en Darkroom 2.01.1354 op de andere booth-pc zijn hier niet fysiek getest.

## Verificatie

De native testvensters registreren of een instellingenopdracht, dropdownwijziging of toets synchroon is verstuurd. Een gevoelige instellingenhandler weigert die aanroepcontext; de normale wachtrijroute slaagt. Een message filter controleert dat de ISO-toetsen daadwerkelijk door de applicatieberichtenlus gaan. Verder testen we trage pagina's, een pagina die niet verandert, reeds geselecteerde Originals, teruggedraaide ISO-wijzigingen en dat bij een vaststaande pagina precies één Next-opdracht wordt verstuurd.

De Windows-semantiek is beschreven in Microsofts documentatie van [SendMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagew) en [PostMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew).

## Installeren

Installeer 24.6.6. Vanaf een geïnstalleerde lichtregeling 24.6.5 kan Darkroom openblijven tijdens de update. Bij oudere versies kan de directe installer worden gebruikt nadat alleen JvdP Lichtregeling via het traymenu is afgesloten. Een al vastgelopen Darkroom-proces wordt door deze update niet gerepareerd of geforceerd herstart; herstel Darkroom voordat je opnieuw test.
