# Trovami

*Trovami* ("find me") remembers where your things are stored. Locations form a tree (House › Cellar › Rack 2 › Blue box), items sit at a location with a photo, and one search finds anything with its full path. Households are shared with the people you live with.

- **Web app** (Blazor): organize locations, sort the Inbox, search, print QR labels, manage households.
- **Android app** (coming): scan an NFC tag or QR label, take a photo, done. Works offline in the cellar and syncs later.
- **AI naming**: an item added with just a photo gets its name from Claude or ChatGPT, with the household's own key.

## Run it

```yaml
services:
  trovami:
    image: ghcr.io/geeforceone/trovami:latest
    ports:
      - "8337:8080"
    volumes:
      - trovami-data:/data
    restart: unless-stopped

volumes:
  trovami-data:
```

The first person to open the app creates the admin account. Everything (database, photos, keys, backups) lives in the `/data` volume.

## Develop

```
dotnet run --project src/Trovami.Web     # http://localhost:5337, data in ./data
dotnet test tests/Trovami.Tests
```

## License

MIT
