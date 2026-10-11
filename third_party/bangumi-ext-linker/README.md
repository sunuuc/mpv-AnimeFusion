# BangumiExtLinker

Try to associate the animation entries in [Bangumi 番组计划](https://bgm.tv/) with the corresponding IDs on other metadata-providing websites (aka **Info Site**) such as [Douban](https://movie.douban.com/), [IMDb](https://www.imdb.com/) et al.

> [!NOTE]
> This Repo based on Bangumi ID, However different metadata-providing websites may split the same work into different entries when handling it. 
> When A Bangumi entry may be split into two or more entries on other metadata-providing websites, only one of the Linked IDs will be marked, and other IDs may be ignored.

**All data comes from the Internet and is for learning purposes only!**

## Update Status

- Last update at: `2026-10-08 01:30:24` (CET, UTC+01:00)
- Last data summary:

| Info Site | Count | Link Format |
|:----:|----:|:----|
| Bangumi | 23206 | `https://bgm.tv/subject/{bgm_id:\d+}` |
| Douban | 18959 | `https://movie.douban.com/subject/{douban_id:\d+}/` |
| Bilibili | 3393 | `https://www.bilibili.com/bangumi/media/{bili_id:md\d+}/` |
| AniDB | 10661 | `https://anidb.net/anime/{anidb_id:\d+}` |
| MyAnimeList | 12324 | `https://myanimelist.net/anime/{mal_id:\d+}` |
| TMDB | 5568 | `https://www.themoviedb.org/{tmdb_id:tv/\d+(/season/\d+(/episode/\d+)?)?\|movie/\d+}` |
| IMDb | 6138 | `https://www.imdb.com/title/{imdb_id:tt\d+}/` |
| TheTVDB | 6748 | `https://thetvdb.com/dereferrer/series/{tvdb_id:\d+}` |
| Wikidata | 3530 | `https://www.wikidata.org/wiki/{wikidata_id:Q\d+}` |

## Data Schema

- [/data/anime_map.json](/data/anime_map.json) ([Github Pages](https://rhilip.github.io/BangumiExtLinker/data/anime_map.json))

```json5
[
    {
        // Content from bangumi/Archive Subject ( type=2 && nsfw=0 )
        "name": "葬送のフリーレン",
        "name_cn": "葬送的芙莉莲",
        "date": "2023-09",
        "bgm_id": "400602",
        // The Linked Id in other metadata-providing websites ( The following field will not exist when mis-match )
        "douban_id": "36093351",
        "bili_id": "md21087073",
        "anidb_id": "17617",
        "mal_id": "52991",
        "tmdb_id": "tv/209867/season/1",
        "imdb_id": "tt22248376",
        "tvdb_id": "424536",
        "wikidata_id": "Q115792176"
    },
    // More.....
]
```


## Appreciation

- [bangumi/Archive](https://github.com/bangumi/Archive): Bangumi Wiki Archive ([CC BY-SA](https://bgm.tv/about/copyright#columnA))
- [czy0729/Bangumi-Subject](https://github.com/czy0729/Bangumi-Subject): Static data snapshot of Bangumi hot subject
- [bangumi-data/bangumi-data](https://github.com/bangumi-data/bangumi-data): Raw data for Japanese Anime ([CC BY 4.0](https://github.com/bangumi-data/bangumi-data#license))
- [manami-project/anime-offline-database](https://github.com/manami-project/anime-offline-database): A JSON based anime dataset containing the most important meta data as well as cross references to various anime sites such as MAL, ANIDB, ANILIST, KITSU and more... ([AGPL-3.0](https://github.com/manami-project/anime-offline-database/blob/master/LICENSE))
- [Anime-Lists/anime-lists](https://github.com/Anime-Lists/anime-lists): Anime mapping lists from AniDB to TheTVDB, TMDB, IMDb.
- [ourbits/PtGen](https://github.com/ourbits/PtGen): The static export of the PtGen Database. ([MIT](https://github.com/ourbits/PtGen/blob/main/LICENSE))

## License

The data in this repo is available for use under a [CC BY 4.0 license](http://creativecommons.org/licenses/by/4.0/). For works with existing copyrights, handle them in accordance with the [Fair Use](http://en.wikipedia.org/wiki/Fair_use) principle and indicate the source.