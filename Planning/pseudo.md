(UI does this) check current token not expired (last expired file) (with small buffer for latency accounting).

if never created (refresh token nonexistent), get user client credentials (ask for developer client id and client secret):
```
1. Generate a random `state` string (for security)

2. Build the auth URL:
   https://accounts.spotify.com/authorize
     ?client_id=YOUR_ID
     &response_type=code
     &redirect_uri=http://localhost:5000/callback
     &scope=playlist-read-private
     &state=YOUR_STATE

3. Open that URL in the user's browser

4. Start an HttpListener on http://localhost:5000/callback

5. Wait for the callback request to come in
   - Extract `code` and `state` from the query params
   - Verify `state` matches what you generated
   - Send a "you can close this tab" HTML response back

6. POST to https://accounts.spotify.com/api/token with:
   - grant_type=authorization_code
   - code=THE_CODE
   - redirect_uri=http://localhost:5000/callback
   - client_id=YOUR_ID
   - client_secret=YOUR_SECRET

7. Parse the response to get:
   - access_token  ← use this for API calls
   - refresh_token ← save this to get new tokens later
   - expires_in    ← 3600 seconds (1 hour)
```
ask if user wants to save refresh token?

if refresh token exists and old access token expired:
```
POST https://accounts.spotify.com/api/token
  grant_type=refresh_token
  refresh_token=YOUR_REFRESH_TOKEN
  client_id=YOUR_ID
  client_secret=YOUR_SECRET
```

then set new token to this one and update last expired file

---
CLI tool:
--- 
inputs:
- authorisation access token  
- spotify playlist url  
- output directory
- sensitivity level
- search depth
- manual mode

