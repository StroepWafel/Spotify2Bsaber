(UI does this) check current token not expired (last expired file) (with small buffer for latency accounting).

if expired, get user client credentials:
```ps
curl -X POST "https://accounts.spotify.com/api/token" \
     -H "Content-Type: application/x-www-form-urlencoded" \
     -d "grant_type=client_credentials&client_id=your-client-id&client_secret=your-client-secret"
```
if client id and client secret unavailable, ask user for them, also ask if they want to save to file for future use. 

access token valid for 1h:
```json
{
  "access_token": "[REDACTED]",
  "token_type": "Bearer",
  "expires_in": 3600
}
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

