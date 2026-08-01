import { useEffect, useState } from 'react'
import './PersonPicker.css'

type Person={id:string;userName:string;displayName:string;email:string;title:string;roles:string[]}

/// What a chosen reviewer is: a person, and what they do here.
/// Account handles are database references, not how a colleague identifies a reviewer; the UI keeps the
/// person’s title and Program authority visible instead.
const describe=(person:Person)=>[person.title,...person.roles.filter(role=>role!==person.title)].filter(Boolean).join(' · ')

export default function PersonPicker({api,projectId,value,name,index,label,excludeUserNames=[],onSelect}:{
 api:string;projectId:string;value:string;name:string;index:number;label?:string;excludeUserNames?:string[];
 onSelect:(person:{userId:string;name:string})=>void
}){
 const [query,setQuery]=useState(name||value),[people,setPeople]=useState<Person[]>([]),[open,setOpen]=useState(false),[chosen,setChosen]=useState<Person>()
 const excluded=new Set(excludeUserNames.map(userName=>userName.toLowerCase()))
 const available=people.filter(person=>!excluded.has(person.userName.toLowerCase()))
 useEffect(()=>{const timer=setTimeout(async()=>{if(query.trim().length<1){setPeople([]);return}const response=await fetch(`${api}/api/directory?projectId=${projectId}&search=${encodeURIComponent(query)}&limit=10`);if(response.ok)setPeople(await response.json())},150);return()=>clearTimeout(timer)},[api,projectId,query])
 return <div className="personPicker"><input aria-label={label??`Approver ${index+1} search`} value={query} placeholder="Search name, title, or role…" autoComplete="off" onFocus={()=>setOpen(true)} onChange={event=>{setQuery(event.target.value);setOpen(true);setChosen(undefined);onSelect({userId:'',name:''})}}/>{value&&chosen&&describe(chosen)&&<small>{describe(chosen)}</small>}{open&&available.length>0&&<div className="personSuggestions">{available.map(person=><button type="button" key={person.id} data-user-name={person.userName} onClick={()=>{setChosen(person);onSelect({userId:person.userName,name:person.displayName});setQuery(person.displayName);setOpen(false)}}><i>{person.displayName.split(' ').map(x=>x[0]).join('').slice(0,2)}</i><span><b>{person.displayName}</b><small>{person.title}</small></span><em>{person.roles.join(' · ')}</em></button>)}</div>}</div>
}
